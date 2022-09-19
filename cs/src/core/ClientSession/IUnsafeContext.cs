﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

namespace FASTER.core
{
    /// <summary>
    /// Manual epoch control functions. Useful when doing generic operations across diverse <see cref="LockableUnsafeContext{Key, Value, Input, Output, Context, Functions}"/> 
    /// and <see cref="UnsafeContext{Key, Value, Input, Output, Context, Functions}"/> specializations.
    /// </summary>
    public interface IUnsafeContext
    {
        /// <summary>
        /// Resume session on current thread. IMPORTANT: Call SuspendThread before any async op.
        /// </summary>
        void ResumeThread();

        /// <summary>
        /// Suspend session on current thread
        /// </summary>
        void SuspendThread();

        /// <summary>
        /// Current epoch of the session
        /// </summary>
        long LocalCurrentEpoch { get; }
    }
}
