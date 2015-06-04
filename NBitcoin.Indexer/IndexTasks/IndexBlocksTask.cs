﻿using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Tasks;

namespace NBitcoin.Indexer.IndexTasks
{
    public class IndexBlocksTask : IndexTask<BlockInfo>
    {
        public IndexBlocksTask(IndexerConfiguration configuration)
            : base(configuration)
        {
        }
        protected override int PartitionSize
        {
            get
            {
                return 1;
            }
        }

        volatile int _IndexedBlocks;
        public int IndexedBlocks
        {
            get
            {
                return _IndexedBlocks;
            }
        }


        public void Index(params Block[] blocks)
        {
            try
            {
                IndexAsync(blocks).Wait();
            }
            catch (AggregateException aex)
            {
                ExceptionDispatchInfo.Capture(aex.InnerException).Throw();
                throw;
            }
        }

        public Task IndexAsync(params Block[] blocks)
        {
            var tasks = blocks
                .Select(b => new Task(() => IndexCore("o", new[]{new BlockInfo()
                {
                    Block = b,
                    BlockId = b.GetHash()
                }})))
                .ToArray();
            foreach (var t in tasks)
                t.Start();
            return Task.WhenAll(tasks);

        }

        protected async override Task EnsureSetup()
        {
            await Configuration.GetBlocksContainer().CreateIfNotExistsAsync().ConfigureAwait(false);
        }
        protected override void ProcessBlock(BlockInfo block, BulkImport<BlockInfo> bulk)
        {
            bulk.Add("o", block);
        }

        protected override void IndexCore(string partitionName, IEnumerable<BlockInfo> blocks)
        {
            var first = blocks.First();
            var block = first.Block;
            var hash = first.BlockId.ToString();

            Stopwatch watch = new Stopwatch();
            watch.Start();
            while (true)
            {
                var container = Configuration.GetBlocksContainer();
                var client = container.ServiceClient;
                client.DefaultRequestOptions.SingleBlobUploadThresholdInBytes = 32 * 1024 * 1024;
                var blob = container.GetPageBlobReference(hash);
                MemoryStream ms = new MemoryStream();
                block.ReadWrite(ms, true);
                var blockBytes = ms.GetBuffer();

                long length = 512 - (ms.Length % 512);
                if (length == 512)
                    length = 0;
                Array.Resize(ref blockBytes, (int)(ms.Length + length));

                try
                {
                    blob.UploadFromByteArray(blockBytes, 0, blockBytes.Length, new AccessCondition()
                    {
                        //Will throw if already exist, save 1 call
                        IfNotModifiedSinceTime = DateTimeOffset.MinValue
                    }, new BlobRequestOptions()
                    {
                        MaximumExecutionTime = _Timeout,
                        ServerTimeout = _Timeout
                    });
                    watch.Stop();
                    IndexerTrace.BlockUploaded(watch.Elapsed, blockBytes.Length);
                    _IndexedBlocks++;
                    break;
                }
                catch (StorageException ex)
                {
                    var alreadyExist = ex.RequestInformation != null && ex.RequestInformation.HttpStatusCode == 412;
                    if (!alreadyExist)
                    {
                        IndexerTrace.ErrorWhileImportingBlockToAzure(new uint256(hash), ex);
                        throw;
                    }
                    watch.Stop();
                    IndexerTrace.BlockAlreadyUploaded();
                    _IndexedBlocks++;
                    break;
                }
                catch (Exception ex)
                {
                    IndexerTrace.ErrorWhileImportingBlockToAzure(new uint256(hash), ex);
                    throw;
                }
            }
        }
    }
}
