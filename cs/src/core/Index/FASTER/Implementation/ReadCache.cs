﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using static FASTER.core.Utility;

namespace FASTER.core
{
    // Partial file for readcache functions
    public unsafe partial class FasterKV<Key, Value> : FasterBase, IFasterKV<Key, Value>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool FindInReadCache(ref Key key, ref OperationStackContext<Key, Value> stackCtx, long minAddress = Constants.kInvalidAddress, bool alwaysFindLatestLA = true)
        {
            Debug.Assert(UseReadCache, "Should not call FindInReadCache if !UseReadCache");

            // minAddress, if present, comes from the pre-pendingIO entry.Address; there may have been no readcache entries then.
            minAddress = IsReadCache(minAddress) ? AbsoluteAddress(minAddress) : readcache.HeadAddress;

        RestartChain:

            // 'recSrc' has already been initialized to the address in 'hei'.
            if (!stackCtx.hei.IsReadCache)
                return false;

            // This is also part of the initialization process for stackCtx.recSrc for each API/InternalXxx call.
            stackCtx.recSrc.LogicalAddress = Constants.kInvalidAddress;
            stackCtx.recSrc.PhysicalAddress = 0;

            stackCtx.recSrc.LatestLogicalAddress &= ~Constants.kReadCacheBitMask;

            while (true)
            {
                if (ReadCacheNeedToWaitForEviction(ref stackCtx))
                    goto RestartChain;

                // Increment the trailing "lowest read cache" address (for the splice point). We'll look ahead from this to examine the next record.
                stackCtx.recSrc.LowestReadCacheLogicalAddress = stackCtx.recSrc.LatestLogicalAddress;
                stackCtx.recSrc.LowestReadCachePhysicalAddress = readcache.GetPhysicalAddress(stackCtx.recSrc.LowestReadCacheLogicalAddress);

                // Use a non-ref local, because we update it below to remove the readcache bit.
                RecordInfo recordInfo = readcache.GetInfo(stackCtx.recSrc.LowestReadCachePhysicalAddress);

                // When traversing the readcache, we skip Invalid records. The semantics of Seal are that the operation is retried, so if we leave
                // Sealed records in the readcache, we'll never get past them. Therefore, we use Invalid to mark a ReadCache record as closed.
                // Return true if we find a Valid read cache entry matching the key.
                if (!recordInfo.Invalid && stackCtx.recSrc.LatestLogicalAddress >= minAddress && !stackCtx.recSrc.HasReadCacheSrc
                    && comparer.Equals(ref key, ref readcache.GetKey(stackCtx.recSrc.LowestReadCachePhysicalAddress)))
                {
                    // Keep these at the current readcache location; they'll be the caller's source record.
                    stackCtx.recSrc.LogicalAddress = stackCtx.recSrc.LowestReadCacheLogicalAddress;
                    stackCtx.recSrc.PhysicalAddress = stackCtx.recSrc.LowestReadCachePhysicalAddress;
                    stackCtx.recSrc.HasReadCacheSrc = true;
                    stackCtx.recSrc.Log = readcache;

                    // Read() does not need to continue past the found record; updaters need to continue to find latestLogicalAddress and lowestReadCache*Address.
                    if (!alwaysFindLatestLA)
                        return true;
                }

                // Is the previous record a main log record? If so, break out.
                if (!recordInfo.PreviousAddressIsReadCache)
                {
                    Debug.Assert(recordInfo.PreviousAddress >= hlog.BeginAddress, "Read cache chain should always end with a main-log entry");
                    stackCtx.recSrc.LatestLogicalAddress = recordInfo.PreviousAddress;
                    goto InMainLog;
                }

                stackCtx.recSrc.LatestLogicalAddress = recordInfo.PreviousAddress & ~Constants.kReadCacheBitMask;
            }

        InMainLog:
            if (stackCtx.recSrc.HasReadCacheSrc)
            { 
                Debug.Assert(object.ReferenceEquals(stackCtx.recSrc.Log, readcache), "Expected Log == readcache");
                return true;
            }

            // We did not find the record in the readcache, so set these to the start of the main log entries, and the caller will call TracebackForKeyMatch
            Debug.Assert(object.ReferenceEquals(stackCtx.recSrc.Log, hlog), "Expected Log == hlog");
            stackCtx.recSrc.LogicalAddress = stackCtx.recSrc.LatestLogicalAddress;
            stackCtx.recSrc.PhysicalAddress = 0; // do *not* call hlog.GetPhysicalAddress(); LogicalAddress may be below hlog.HeadAddress. Let the caller decide when to do this.
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool ReadCacheNeedToWaitForEviction(ref OperationStackContext<Key, Value> stackCtx)
        {
            if (stackCtx.recSrc.LatestLogicalAddress < readcache.HeadAddress)
            {
                SpinWaitUntilRecordIsClosed(stackCtx.recSrc.LatestLogicalAddress, readcache);

                // Restore to hlog; we may have set readcache into Log and continued the loop, had to restart, and the matching readcache record was evicted.
                stackCtx.UpdateRecordSourceToCurrentHashEntry(hlog);
                return true;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool SpliceIntoHashChainAtReadCacheBoundary(ref RecordSource<Key, Value> recSrc, long newLogicalAddress)
        {
            // Splice into the gap of the last readcache/first main log entries.
            Debug.Assert(recSrc.LowestReadCachePhysicalAddress >= readcache.HeadAddress, "LowestReadCachePhysicalAddress must be >= readcache.HeadAddress; caller should have called VerifyReadCacheSplicePoint");
            ref RecordInfo rcri = ref readcache.GetInfo(recSrc.LowestReadCachePhysicalAddress);
            return rcri.TryUpdateAddress(recSrc.LatestLogicalAddress, newLogicalAddress);
        }

        // Skip over all readcache records in this key's chain, advancing stackCtx.recSrc to the first non-readcache record we encounter.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SkipReadCache(ref OperationStackContext<Key, Value> stackCtx, out bool didRefresh)
        {
            Debug.Assert(UseReadCache, "Should not call SkipReadCache if !UseReadCache");
            didRefresh = false;

        RestartChain:
            // 'recSrc' has already been initialized to the address in 'hei'.
            if (!stackCtx.hei.IsReadCache)
                return;

            // This is FindInReadCache without the key comparison or untilAddress.
            stackCtx.recSrc.LogicalAddress = Constants.kInvalidAddress;
            stackCtx.recSrc.PhysicalAddress = 0;

            stackCtx.recSrc.LatestLogicalAddress &= ~Constants.kReadCacheBitMask;

            while (true)
            {
                if (ReadCacheNeedToWaitForEviction(ref stackCtx))
                {
                    didRefresh = true;
                    goto RestartChain;
                }

                // Increment the trailing "lowest read cache" address (for the splice point). We'll look ahead from this to examine the next record.
                stackCtx.recSrc.LowestReadCacheLogicalAddress = stackCtx.recSrc.LatestLogicalAddress;
                stackCtx.recSrc.LowestReadCachePhysicalAddress = readcache.GetPhysicalAddress(stackCtx.recSrc.LowestReadCacheLogicalAddress);

                RecordInfo recordInfo = readcache.GetInfo(stackCtx.recSrc.LowestReadCachePhysicalAddress);
                if (!recordInfo.PreviousAddressIsReadCache)
                {
                    Debug.Assert(recordInfo.PreviousAddress >= hlog.BeginAddress, "Read cache chain should always end with a main-log entry");
                    stackCtx.recSrc.LatestLogicalAddress = recordInfo.PreviousAddress;
                    stackCtx.recSrc.LogicalAddress = stackCtx.recSrc.LatestLogicalAddress;
                    stackCtx.recSrc.PhysicalAddress = 0;
                    return;
                }
                stackCtx.recSrc.LatestLogicalAddress = recordInfo.PreviousAddress & ~Constants.kReadCacheBitMask;
            }
        }

        // Skip over all readcache records in all key chains in this bucket, updating the bucket to point to the first main log record.
        // Called during checkpointing; we create a copy of the hash table page, eliminate read cache pointers from this copy, then write this copy to disk.
        private void SkipReadCacheBucket(HashBucket* bucket)
        {
            for (int index = 0; index < Constants.kOverflowBucketIndex; ++index)
            {
                HashBucketEntry* entry = (HashBucketEntry*)&bucket->bucket_entries[index];
                if (0 == entry->word)
                    continue;

                if (!entry->ReadCache) continue;
                var logicalAddress = entry->Address;
                var physicalAddress = readcache.GetPhysicalAddress(AbsoluteAddress(logicalAddress));

                while (true)
                {
                    logicalAddress = readcache.GetInfo(physicalAddress).PreviousAddress;
                    entry->Address = logicalAddress;
                    if (!entry->ReadCache)
                        break;
                    physicalAddress = readcache.GetPhysicalAddress(AbsoluteAddress(logicalAddress));
                }
            }
        }

        // Called after a readcache insert, to make sure there was no race with another session that added a main-log record at the same time.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool EnsureNoNewMainLogRecordWasSpliced(ref Key key, RecordSource<Key, Value> recSrc, long untilLogicalAddress, ref OperationStatus failStatus)
        {
            bool success = true;
            ref RecordInfo lowest_rcri = ref readcache.GetInfo(recSrc.LowestReadCachePhysicalAddress);
            Debug.Assert(!lowest_rcri.PreviousAddressIsReadCache, "lowest-rcri.PreviousAddress should be a main-log address");
            if (lowest_rcri.PreviousAddress > untilLogicalAddress)
            {
                // Someone added a new record in the splice region. It won't be readcache; that would've been added at tail. See if it's our key.
                var minAddress = untilLogicalAddress > hlog.HeadAddress ? untilLogicalAddress : hlog.HeadAddress;
                if (TraceBackForKeyMatch(ref key, lowest_rcri.PreviousAddress, minAddress + 1, out long prevAddress, out _))
                    success = false;
                else if (prevAddress > untilLogicalAddress && prevAddress < hlog.HeadAddress)
                {
                    // One or more records were inserted and escaped to disk during the time of this Read/PENDING operation, untilLogicalAddress
                    // is below hlog.HeadAddress, and there are one or more inserted records between them:
                    //     hlog.HeadAddress -> [prevAddress is somewhere in here] -> untilLogicalAddress
                    // (If prevAddress is == untilLogicalAddress, we know there is nothing more recent, so the new readcache record should stay.)
                    // recSrc.HasLockTableLock may or may not be true. The new readcache record must be invalidated; then we return ON_DISK;
                    // this abandons the attempt to CopyToTail, and the caller proceeds with the possibly-stale value that was read.
                    success = false;
                    failStatus = OperationStatus.RECORD_ON_DISK;
                }
            }
            return success;
        }

        // Called to check if another session added a readcache entry from a pending read while we were inserting a record at the tail of the log.
        // If so, then it must be invalidated, and its *read* locks must be transferred to the new record. Why not X locks?
        //      - There can be only one X lock so we optimize its handling in CompleteUpdate, rather than transfer them like S locks (because there
        //        can be multiple S locks).
        //      - The thread calling this has "won the CAS" if it has gotten this far; this is, it has CAS'd in a new record at the tail of the log
        //        (or spliced it at the end of the readcache prefix chain).
        //          - It is still holding its "tentative" X lock on the newly-inserted log-tail record while calling this.
        //          - If there is another thread holding an X lock on this readcache record, it will fail its CAS, give up its X lock, and RETRY_LATER.
        // Note: The caller will do no epoch-refreshing operations after re-verifying the readcache chain following record allocation, so it is not
        // possible for the chain to be disrupted and the new insertion lost, even if readcache.HeadAddress is raised above hei.Address.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReadCacheCheckTailAfterSplice(ref Key key, ref HashEntryInfo hei, ref RecordInfo newRecordInfo)
        {
            Debug.Assert(UseReadCache, "Should not call ReadCacheCompleteInsertAtTail if !UseReadCache");

            // We already searched from hei.Address down; so now we search from hei.CurrentAddress down to just above hei.Address.
            HashBucketEntry entry = new() { word = hei.CurrentAddress | (hei.IsCurrentReadCache ? Constants.kReadCacheBitMask : 0)};
            HashBucketEntry untilEntry = new() { word = hei.Address | (hei.IsReadCache ? Constants.kReadCacheBitMask : 0) };

            // Traverse for the key above untilAddress (which may not be in the readcache if there were no readcache records when it was retrieved).
            while (entry.ReadCache && (entry.Address > untilEntry.Address || !untilEntry.ReadCache))
            {
                var physicalAddress = readcache.GetPhysicalAddress(entry.AbsoluteAddress);
                ref RecordInfo recordInfo = ref readcache.GetInfo(physicalAddress);
                if (!recordInfo.Invalid && comparer.Equals(ref key, ref readcache.GetKey(physicalAddress)))
                {
                    // We don't release the ephemeral lock because we don't have a lock on this readcache record; it sneaked in behind us.
                    newRecordInfo.CopyReadLocksFromAndMarkSourceAtomic(ref recordInfo, seal: false, removeEphemeralLock: false);
                    return;
                }
                entry.word = recordInfo.PreviousAddress;
            }

            // If we're here, no (valid) record for 'key' was found.
            return;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ReadCacheAbandonRecord(long physicalAddress)
        {
            // TODO: We currently don't save readcache allocations for retry, but we could
            ref var ri = ref readcache.GetInfo(physicalAddress);
            ri.SetInvalid();
            ri.PreviousAddress = Constants.kTempInvalidAddress;     // Necessary for ReadCacheEvict, but cannot be kInvalidAddress or we have recordInfo.IsNull
        }

        internal void ReadCacheEvict(long rcLogicalAddress, long rcToLogicalAddress)
        {
            // Iterate readcache entries in the range rcFrom/ToLogicalAddress, and remove them from the hash chain.
            while (rcLogicalAddress < rcToLogicalAddress)
            {
                var rcPhysicalAddress = readcache.GetPhysicalAddress(rcLogicalAddress);
                var (_, rcAllocatedSize) = readcache.GetRecordSize(rcPhysicalAddress);
                var rcRecordInfo = readcache.GetInfo(rcPhysicalAddress);

                // Check PreviousAddress for null to handle the info.IsNull() "partial record at end of page" case as well as readcache CAS failures
                // (such failed records are not in the hash chain, so we must not process them here). We do process other Invalid records here.
                if (rcRecordInfo.PreviousAddress <= Constants.kTempInvalidAddress)
                    goto NextRecord;

                // If there are any readcache entries for this key, the hash chain will always be of the form:
                //                   |----- readcache records -----|    |------ main log records ------|
                //      hashtable -> rcN -> ... -> rc3 -> rc2 -> rc1 -> mN -> ... -> m3 -> m2 -> m1 -> 0

                // This diagram shows that this readcache record's PreviousAddress (in 'entry') is always a lower-readcache or non-readcache logicalAddress,
                // and therefore this record and the entire sub-chain "to the right" should be evicted. The sequence of events is:
                //  1. Get the key from the readcache for this to-be-evicted record.
                //  2. Call FindTag on that key in the main fkv to get the start of the hash chain.
                //  3. Walk the hash chain's readcache entries, removing records in the "to be removed" range.
                //     Do not remove Invalid records outside this range; that leads to race conditions.
                Debug.Assert(!rcRecordInfo.PreviousAddressIsReadCache || rcRecordInfo.AbsolutePreviousAddress < rcLogicalAddress, "Invalid record ordering in readcache");

                // Find the hash index entry for the key in the FKV's hash table.
                ref Key key = ref readcache.GetKey(rcPhysicalAddress);
                HashEntryInfo hei = new(comparer.GetHashCode64(ref key));
                if (!FindTag(ref hei))
                    goto NextRecord;

                // Traverse the chain of readcache entries for this key, looking "ahead" to .PreviousAddress to see if it is less than readcache.HeadAddress.
                // nextPhysicalAddress remains Constants.kInvalidAddress if hei.Address is < HeadAddress; othrwise, it is the lowest-address readcache record
                // remaining following this eviction, and its .PreviousAddress is updated to each lower record in turn until we hit a non-readcache record.
                long nextPhysicalAddress = Constants.kInvalidAddress;
                HashBucketEntry entry = new() { word = hei.entry.word };
                while (entry.ReadCache)
                {
                    var la = entry.AbsoluteAddress;
                    var pa = readcache.GetPhysicalAddress(la);
                    ref RecordInfo ri = ref readcache.GetInfo(pa);

#if DEBUG
                    // Due to collisions, we can compare the hash code *mask* (i.e. the hash bucket index), not the key
                    var mask = state[resizeInfo.version].size_mask;
                    var rc_mask = hei.hash & mask;
                    var pa_mask = comparer.GetHashCode64(ref readcache.GetKey(pa)) & mask;
                    Debug.Assert(rc_mask == pa_mask, "The keyHash mask of the hash-chain ReadCache entry does not match the one obtained from the initial readcache address");
#endif

                    // If the record's address is above the eviction range, leave it there and track nextPhysicalAddress.
                    if (la >= rcToLogicalAddress)
                    {
                        nextPhysicalAddress = pa;
                        entry.word = ri.PreviousAddress;
                        continue;
                    }

                    // The record is being evicted. If we have a higher readcache record that is not being evicted, unlink 'la' by setting
                    // (nextPhysicalAddress).PreviousAddress to (la).PreviousAddress.
                    if (nextPhysicalAddress != Constants.kInvalidAddress)
                    {
                        ref RecordInfo nextri = ref readcache.GetInfo(nextPhysicalAddress);
                        if (nextri.TryUpdateAddress(entry.Address, ri.PreviousAddress))
                            ri.PreviousAddress = Constants.kTempInvalidAddress;     // The record is no longer in the chain
                        entry.word = nextri.PreviousAddress;
                        continue;
                    }

                    // We are evicting the record whose address is in the hash bucket; unlink 'la' by setting the hash bucket to point to (la).PreviousAddress.
                    if (hei.TryCAS(ri.PreviousAddress))
                        ri.PreviousAddress = Constants.kTempInvalidAddress;     // The record is no longer in the chain
                    else
                        hei.SetToCurrent();
                    entry.word = hei.entry.word;
                }

            NextRecord:
                if ((rcLogicalAddress & readcache.PageSizeMask) + rcAllocatedSize > readcache.PageSize)
                {
                    rcLogicalAddress = (1 + (rcLogicalAddress >> readcache.LogPageSizeBits)) << readcache.LogPageSizeBits;
                    continue;
                }
                rcLogicalAddress += rcAllocatedSize;
            }
        }
    }
}