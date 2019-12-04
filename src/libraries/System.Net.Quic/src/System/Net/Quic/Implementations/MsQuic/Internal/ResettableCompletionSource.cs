﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information

using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace System.Net.Quic.Implementations.MsQuic.Internal
{
    /// <summary>
    /// A resettable completion source which can be completed multiple times.
    /// Used to make methods async between completed events and their associated async method.
    /// </summary>
    internal abstract class ResettableCompletionSource<T> : IValueTaskSource<T>
    {
        protected ManualResetValueTaskSourceCore<T> _valueTaskSource;

        public ResettableCompletionSource()
        {
            _valueTaskSource.RunContinuationsAsynchronously = true;
        }

        public ValueTask<T> GetValueTask()
        {
            return new ValueTask<T>(this, _valueTaskSource.Version);
        }

        public abstract T GetResult(short token);

        public ValueTaskSourceStatus GetStatus(short token)
        {
            return _valueTaskSource.GetStatus(token);
        }

        public void OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            _valueTaskSource.OnCompleted(continuation, state, token, flags);
        }

        public void Complete(T result)
        {
            _valueTaskSource.SetResult(result);
        }

        public void CompleteException(Exception ex)
        {
            _valueTaskSource.SetException(ex);
        }
    }
}
