﻿using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB
{
    /// <summary>
    /// Abstract class with workflow method to be used in pipeline implementation
    /// </summary>
    internal abstract class BasePipe : IDisposable
    {
        protected readonly LiteEngine _engine;
        protected readonly LiteTransaction _transaction;
        protected readonly IDocumentLoader _loader;

        private LiteTransaction _tempTransaction = null;

        public BasePipe(LiteEngine engine, LiteTransaction transaction, IDocumentLoader loader)
        {
            _engine = engine;
            _transaction = transaction;
            _loader = loader;
        }

        /// <summary>
        /// Abstract method to be implement according pipe workflow
        /// </summary>
        public abstract IEnumerable<BsonValue> Pipe(IEnumerable<IndexNode> nodes, QueryPlan query);

        // load documents from disk or make a "fake" document using key only (useful for COUNT/EXISTS)
        protected IEnumerable<BsonDocument> LoadDocument(IEnumerable<IndexNode> nodes, bool keyOnly, string name)
        {
            foreach (var node in nodes)
            {
                yield return keyOnly ?
                    new BsonDocument { [name] = node.Key } :
                    _loader.Load(node.DataBlock);
            }
        }

        /// <summary>
        /// Pipe: Do include in result document according path expression
        /// </summary>
        protected IEnumerable<BsonDocument> Include(IEnumerable<BsonDocument> source, BsonExpression path)
        {
            // cached services
            string last = null;
            Snapshot snapshot = null;
            IndexService indexer = null;
            DataService data = null;
            CollectionIndex index = null;
            IDocumentLoader loader = null;

            foreach (var doc in source)
            {
                foreach (var value in path.Execute(doc, false)
                                        .Where(x => x.IsDocument)
                                        .Select(x => x.AsDocument)
                                        .ToList())
                {
                    // works only if is a document
                    var refId = value["$id"];
                    var refCol = value["$ref"];

                    // if has no reference, just go out
                    if (refId.IsNull || !refCol.IsString) continue;

                    // do some cache re-using when is same $ref (almost always is the same $ref collection)
                    if (last != refCol.AsString)
                    {
                        last = refCol.AsString;

                        // initialize services
                        snapshot = _transaction.CreateSnapshot(SnapshotMode.Read, last, false);
                        indexer = new IndexService(snapshot);
                        data = new DataService(snapshot);

                        //TODO: oferecer suporte a include com campos selecionados!!
                        loader = new DocumentLoader(data, _engine.UtcDate, null);

                        index = snapshot.CollectionPage?.PK;
                    }

                    // if there is no ref collection
                    if (index == null)
                    {
                        value.Destroy();
                    }
                    else
                    {
                        var node = indexer.Find(index, refId, false, Query.Ascending);

                        // if _id was not found in $ref collection, remove value
                        if (node == null)
                        {
                            value.Destroy();
                        }
                        else
                        {
                            // load document based on dataBlock position
                            var refDoc = loader.Load(node.DataBlock);

                            value.Remove("$id");
                            value.Remove("$ref");
                            
                            refDoc.CopyTo(value);
                        }
                    }
                }

                yield return doc;
            }
        }

        /// <summary>
        /// Pipe: Filter document according expression. Expression must be an Bool result
        /// </summary>
        protected IEnumerable<BsonDocument> Filter(IEnumerable<BsonDocument> source, BsonExpression expr)
        {
            foreach(var doc in source)
            {
                var result = expr.Execute(doc, true).FirstOrDefault();

                // expression must return an boolean and be true to return document
                if (result.IsBoolean && result.AsBoolean == true)
                {
                    yield return doc;
                }
            }
        }

        /// <summary>
        /// Pipe: Transaform final result appling expressin transform. Expression must return an BsonDocument (or will be converter into a new documnet)
        /// </summary>
        protected IEnumerable<BsonDocument> Select(IEnumerable<BsonDocument> source, BsonExpression expr)
        {
            foreach(var doc in source)
            {
                var result = expr.Execute(doc, true);

                var value = result.First();

                if (value.IsDocument)
                {
                    yield return value.AsDocument;
                }
                else
                {
                    yield return new BsonDocument { ["expr"] = value };
                }
            }
        }

        /// <summary>
        /// Pipe: OrderBy documents according orderby expression/order
        /// </summary>
        protected IEnumerable<BsonDocument> OrderBy(IEnumerable<BsonDocument> source, BsonExpression expr, int order, int offset, int limit)
        {
            // using tempdb for store sort data
            if (_tempTransaction == null)
            {
                _tempTransaction = _engine.TempDB.GetTransaction(out var isNew);
            }

            // create snapshot from temp transaction
            var snapshot = _tempTransaction.CreateSnapshot(SnapshotMode.Read, Guid.NewGuid().ToString("n"), false);

            return DoOrderBy();

            IEnumerable<BsonDocument> DoOrderBy()
            {
                var indexer = new IndexService(snapshot);

                // create new page as collection page (with no CollectionListPage reference)
                var col = snapshot.NewPage<CollectionPage>();
                var index = indexer.CreateIndex(col);

                // get head/tail index node
                var head = indexer.GetNode(index.HeadNode);
                var tail = indexer.GetNode(index.TailNode);

                var last = order == Query.Ascending ? BsonValue.MaxValue : BsonValue.MinValue;
                var total = limit == int.MaxValue ? int.MaxValue : offset + limit;
                var indexCounter = 0;

                foreach (var doc in source)
                {
                    // get key to be sorted
                    var key = expr.Execute(doc, true).First();
                    var diff = key.CompareTo(last);

                    // add to list only if lower than last space
                    if ((order == Query.Ascending && diff < 1) ||
                        (order == Query.Descending && diff > -1))
                    {
                        var tmpNode = indexer.AddNode(index, key, null);

                        // use rawId (position of document inside datafile)
                        tmpNode.DataBlock = doc.RawId;
                        tmpNode.CacheDocument = doc;

                        indexCounter++;

                        // exceeded limit
                        if (indexCounter > total)
                        {
                            var exceeded = (order == Query.Ascending) ? tail.Prev[0] : head.Next[0];

                            indexer.Delete(index, exceeded);

                            var lnode = (order == Query.Ascending) ? tail.Prev[0] : head.Next[0];

                            last = indexer.GetNode(lnode).Key;

                            indexCounter--;
                        }

                        // if memory pages excedded limit size, flush to temp disk
                        _tempTransaction.Safepoint();
                    }
                }

                var find = indexer.FindAll(index, order).Skip(offset).Take(limit);

                foreach (var node in find)
                {
                    // if document are in cache, use it. if not, get from disk again
                    var doc = node.CacheDocument;

                    if (doc == null)
                    {
                        doc = _loader.Load(node.DataBlock);
                    }

                    yield return doc;

                    _tempTransaction.Safepoint();
                }
            }
        }

        public void Dispose()
        {
            // if temp transaction was used, abort (no not save) here
            _tempTransaction?.Abort();
        }
    }
}