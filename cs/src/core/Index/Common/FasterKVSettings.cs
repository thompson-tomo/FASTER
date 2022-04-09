﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.IO;

namespace FASTER.core
{
    /// <summary>
    /// Configuration settings for hybrid log. Use Utility.ParseSize to specify sizes in familiar string notation (e.g., "4k" and "4 MB").
    /// </summary>
    public sealed class FasterKVSettings<Key, Value> : IDisposable
    {
        readonly bool disposeDevices = false;
        readonly bool deleteDirOnDispose = false;
        readonly string baseDir;

        /// <summary>
        /// Size of main hash index, in bytes. Rounds down to power of 2.
        /// </summary>
        /// <remarks>Equivalent of old "cache lines" value &lt;&lt; 6.</remarks>
        public long IndexSize = 1L << 26;

        /// <summary>
        /// Whether FASTER takes read and write locks on records
        /// </summary>
        public bool DisableLocking = false;

        /// <summary>
        /// Device used for main hybrid log
        /// </summary>
        public IDevice LogDevice;

        /// <summary>
        /// Device used for serialized heap objects in hybrid log
        /// </summary>
        public IDevice ObjectLogDevice;

        /// <summary>
        /// Size of a page, in bytes
        /// </summary>
        public long PageSize = 1 << 25;

        /// <summary>
        /// Size of a segment (group of pages), in bytes. Rounds down to power of 2.
        /// </summary>
        public long SegmentSize = 1L << 30;

        /// <summary>
        /// Total size of in-memory part of log, in bytes. Rounds down to power of 2.
        /// </summary>
        public long MemorySize = 1L << 34;

        /// <summary>
        /// Fraction of log marked as mutable (in-place updates). Rounds down to power of 2.
        /// </summary>
        public double MutableFraction = 0.9;

        /// <summary>
        /// Control Read operations. These flags may be overridden by flags specified on session.NewSession or on the individual Read() operations
        /// </summary>
        public ReadFlags ReadFlags;

        /// <summary>
        /// Whether to preallocate the entire log (pages) in memory
        /// </summary>
        public bool PreallocateLog = false;

        /// <summary>
        /// Key serializer
        /// </summary>
        public Func<IObjectSerializer<Key>> KeySerializer;

        /// <summary>
        /// Value serializer
        /// </summary>
        public Func<IObjectSerializer<Value>> ValueSerializer;

        /// <summary>
        /// Equality comparer for key
        /// </summary>
        public IFasterEqualityComparer<Key> EqualityComparer;

        /// <summary>
        /// Info for variable-length keys
        /// </summary>
        public IVariableLengthStruct<Key> KeyLength;

        /// <summary>
        /// Info for variable-length values
        /// </summary>
        public IVariableLengthStruct<Value> ValueLength;

        /// <summary>
        /// Whether read cache is enabled
        /// </summary>
        public bool ReadCacheEnabled = false;

        /// <summary>
        /// Size of a read cache page, in bytes. Rounds down to power of 2.
        /// </summary>
        public long ReadCachePageSize = 1 << 25;

        /// <summary>
        /// Total size of read cache, in bytes. Rounds down to power of 2.
        /// </summary>
        public long ReadCacheMemorySize = 1L << 34;

        /// <summary>
        /// Fraction of log head (in memory) used for second chance 
        /// copy to tail. This is (1 - MutableFraction) for the 
        /// underlying log.
        /// </summary>
        public double ReadCacheSecondChanceFraction = 0.1;

        /// <summary>
        /// Checkpoint manager
        /// </summary>
        public ICheckpointManager CheckpointManager = null;

        /// <summary>
        /// Use specified directory for storing and retrieving checkpoints
        /// using local storage device.
        /// </summary>
        public string CheckpointDir = null;

        /// <summary>
        /// Whether FASTER should remove outdated checkpoints automatically
        /// </summary>
        public bool RemoveOutdatedCheckpoints = true;

        /// <summary>
        /// Try to recover from latest checkpoint, if available
        /// </summary>
        public bool TryRecoverLatest = false;

        /// <summary>
        /// Create default configuration settings for FasterKV. You need to create and specify LogDevice 
        /// explicitly with this API.
        /// Use Utility.ParseSize to specify sizes in familiar string notation (e.g., "4k" and "4 MB").
        /// Default index size is 64MB.
        /// </summary>
        public FasterKVSettings() { }

        /// <summary>
        /// Create default configuration backed by local storage at given base directory.
        /// Use Utility.ParseSize to specify sizes in familiar string notation (e.g., "4k" and "4 MB").
        /// Default index size is 64MB.
        /// </summary>
        /// <param name="baseDir">Base directory (without trailing path separator)</param>
        /// <param name="deleteDirOnDispose">Whether to delete base directory on dispose. This option prevents later recovery.</param>
        public FasterKVSettings(string baseDir, bool deleteDirOnDispose = false)
        {
            disposeDevices = true;
            this.deleteDirOnDispose = deleteDirOnDispose;
            this.baseDir = baseDir;

            LogDevice = baseDir == null ? new NullDevice() : Devices.CreateLogDevice(baseDir + "/hlog.log", deleteOnClose: deleteDirOnDispose);
            if ((!Utility.IsBlittable<Key>() && KeyLength == null) ||
                (!Utility.IsBlittable<Value>() && ValueLength == null))
            {
                ObjectLogDevice = baseDir == null ? new NullDevice() : Devices.CreateLogDevice(baseDir + "/hlog.obj.log", deleteOnClose: deleteDirOnDispose);
            }

            CheckpointDir = baseDir == null ? null : baseDir + "/checkpoints";
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (disposeDevices)
            {
                LogDevice?.Dispose();
                ObjectLogDevice?.Dispose();
                if (deleteDirOnDispose && baseDir != null)
                {
                    try { new DirectoryInfo(baseDir).Delete(true); } catch { }
                }
            }
        }

        /// <inheritdoc />
        public override string ToString()
        {
            var retStr = $"index: {Utility.PrettySize(IndexSize)}; log memory: {Utility.PrettySize(MemorySize)}; log page: {Utility.PrettySize(PageSize)}; log segment: {Utility.PrettySize(SegmentSize)}";
            retStr += $"; log device: {(LogDevice == null ? "null" : LogDevice.GetType().Name)}";
            retStr += $"; obj log device: {(ObjectLogDevice == null ? "null" : ObjectLogDevice.GetType().Name)}";
            retStr += $"; mutable fraction: {MutableFraction}; supports locking: {(DisableLocking ? "no" : "yes")}";
            retStr += $"; read cache (rc): {(ReadCacheEnabled ? "yes" : "no")}";
            if (ReadCacheEnabled)
                retStr += $"; rc memory: {Utility.PrettySize(ReadCacheMemorySize)}; rc page: {Utility.PrettySize(ReadCachePageSize)}";
            return retStr;
        }

        internal long IndexSizeToCacheLines()
        {
            long adjustedSize = Utility.PreviousPowerOf2(IndexSize);
            if (adjustedSize < 64)
                throw new FasterException($"{nameof(IndexSize)} should be at least of size one cache line (64 bytes)");
            if (IndexSize != adjustedSize)
                Trace.TraceInformation($"Warning: using lower value {adjustedSize} instead of specified {IndexSize} for {nameof(IndexSize)}");
            return adjustedSize / 64;
        }

        /// <summary>
        /// Utility function to convert from old constructor style to FasterKVSettings
        /// </summary>
        internal static long IndexSizeFromCacheLines(long cacheLines) => cacheLines * 64;

        internal LogSettings GetLogSettings()
        {
            return new LogSettings
            {
                ReadFlags = ReadFlags,
                LogDevice = LogDevice,
                ObjectLogDevice = ObjectLogDevice,
                MemorySizeBits = Utility.NumBitsPreviousPowerOf2(MemorySize),
                PageSizeBits = Utility.NumBitsPreviousPowerOf2(PageSize),
                SegmentSizeBits = Utility.NumBitsPreviousPowerOf2(SegmentSize),
                MutableFraction = MutableFraction,
                PreallocateLog = PreallocateLog,
                ReadCacheSettings = GetReadCacheSettings()
            };
        }

        private ReadCacheSettings GetReadCacheSettings()
        {
            return ReadCacheEnabled ?
                new ReadCacheSettings
                {
                    MemorySizeBits = Utility.NumBitsPreviousPowerOf2(ReadCacheMemorySize),
                    PageSizeBits = Utility.NumBitsPreviousPowerOf2(ReadCachePageSize),
                    SecondChanceFraction = ReadCacheSecondChanceFraction
                }
                : null;
        }

        internal SerializerSettings<Key, Value> GetSerializerSettings()
        {
            if (KeySerializer == null && ValueSerializer == null)
                return null;

            return new SerializerSettings<Key, Value>
            {
                keySerializer = KeySerializer,
                valueSerializer = ValueSerializer
            };
        }

        internal CheckpointSettings GetCheckpointSettings()
        {
            return new CheckpointSettings
            {
                CheckpointDir = CheckpointDir,
                CheckpointManager = CheckpointManager,
                RemoveOutdated = RemoveOutdatedCheckpoints
            };
        }

        internal VariableLengthStructSettings<Key, Value> GetVariableLengthStructSettings()
        {
            if (KeyLength == null && ValueLength == null)
                return null;

            return new VariableLengthStructSettings<Key, Value>
            {
                keyLength = KeyLength,
                valueLength = ValueLength
            };
        }

    }
}