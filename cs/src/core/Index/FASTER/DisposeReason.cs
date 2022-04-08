﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

namespace FASTER.core
{
    /// <summary>
    /// The reason for a call to <see cref="IStoreFunctions{Key, Value}.Dispose(ref Key, ref Value, DisposeReason)"/>
    /// </summary>
    public enum DisposeReason
    {
        /// <summary>
        /// No Dispose() call was made
        /// </summary>
        None,

        /// <summary>
        /// Failure of SingleWriter insertion of a record at the tail of the cache.
        /// </summary>
        SingleWriterCASFailed,

        /// <summary>
        /// Failure of CopyUpdater insertion of a record at the tail of the cache.
        /// </summary>
        CopyUpdaterCASFailed,

        /// <summary>
        /// Failure of InitialUpdater insertion of a record at the tail of the cache.
        /// </summary>
        InitialUpdaterCASFailed,

        /// <summary>
        /// Failure of SingleDeleter insertion of a record at the tail of the cache.
        /// </summary>
        SingleDeleterCASFailed,

        /// <summary>
        /// A record was deserialized from the disk for a pending Read or RMW operation.
        /// </summary>
        DeserializedFromDisk,

        /// <summary>
        /// A page was evicted from the in-memory portion of the main log, or from the readcache.
        /// </summary>
        PageEviction
    }
}