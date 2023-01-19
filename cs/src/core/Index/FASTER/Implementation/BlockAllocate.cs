﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.Runtime.CompilerServices;

namespace FASTER.core
{
    public unsafe partial class FasterKV<Key, Value> : FasterBase, IFasterKV<Key, Value>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool BlockAllocate<Input, Output, Context>(
                int recordSize,
                out long logicalAddress,
                ref PendingContext<Input, Output, Context> pendingContext,
                out OperationStatus internalStatus)
            => TryBlockAllocate(hlog, recordSize, out logicalAddress, ref pendingContext, out internalStatus);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool BlockAllocateReadCache<Input, Output, Context>(
                int recordSize,
                out long logicalAddress,
                ref PendingContext<Input, Output, Context> pendingContext,
                out OperationStatus internalStatus)
            => TryBlockAllocate(readcache, recordSize, out logicalAddress, ref pendingContext, out internalStatus);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryBlockAllocate<Input, Output, Context>(
                AllocatorBase<Key, Value> allocator,
                int recordSize,
                out long logicalAddress,
                ref PendingContext<Input, Output, Context> pendingContext,
                out OperationStatus internalStatus)
        {
            pendingContext.flushEvent = allocator.FlushEvent;
            logicalAddress = allocator.TryAllocate(recordSize);
            if (logicalAddress > 0)
            {
                pendingContext.flushEvent = default;
                internalStatus = OperationStatus.SUCCESS;
                return true;
            }

            if (logicalAddress == 0)
            {
                // We expect flushEvent to be signaled.
                internalStatus = OperationStatus.ALLOCATE_FAILED;
                return false;
            }

            // logicalAddress is < 0 so we do not expect flushEvent to be signaled; return RETRY_LATER to refresh the epoch.
            pendingContext.flushEvent = default;
            allocator.TryComplete();
            internalStatus = OperationStatus.RETRY_LATER;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void SaveAllocationForRetry<Input, Output, Context>(ref PendingContext<Input, Output, Context> pendingContext, long logicalAddress, long physicalAddress, int allocatedSize)
        {
            ref var recordInfo = ref hlog.GetInfo(physicalAddress);
            recordInfo.SetInvalid();    // so log scan will skip it

            *(int*)Unsafe.AsPointer(ref hlog.GetValue(physicalAddress)) = allocatedSize;
            pendingContext.retryNewLogicalAddress = logicalAddress;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool GetAllocationForRetry<Input, Output, Context>(ref PendingContext<Input, Output, Context> pendingContext, long minAddress, int minSize, out long newLogicalAddress, out long newPhysicalAddress)
        {
            // Use an earlier allocation from a failed operation, if possible.
            newLogicalAddress = pendingContext.retryNewLogicalAddress;
            newPhysicalAddress = 0;
            pendingContext.retryNewLogicalAddress = 0;
            if (newLogicalAddress < hlog.HeadAddress || newLogicalAddress <= minAddress)
                return false;
            newPhysicalAddress = hlog.GetPhysicalAddress(newLogicalAddress);
            int recordSize = *(int*)Unsafe.AsPointer(ref hlog.GetValue(newPhysicalAddress));
            return recordSize >= minSize;
        }
    }
}