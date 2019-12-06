﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Quic.Implementations.MsQuic.Internal;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using static System.Net.Quic.Implementations.MsQuic.Internal.MsQuicNativeMethods;

namespace System.Net.Quic.Implementations.MsQuic
{
    internal sealed class MsQuicStream : QuicStreamProvider
    {
        // Functions to invoke in MsQuic
        private MsQuicApi _api;

        // Pointer to the underlying stream
        private readonly IntPtr _ptr;

        // Handle to this object for native callbacks.
        private GCHandle _handle;

        // Delegate that wraps the static function that will be called when receiving an event.
        private StreamCallbackDelegate _callback;

        // Backing for StreamId
        private long _streamId = -1;

        // Resettable completions to be used for multiple calls to send, start, and shutdown.
        private ResettableCompletionSource<uint> _sendResettableCompletionSource;

        // Resettable completions to be used for multiple calls to receive.
        private ResettableCompletionSource<uint> _receiveResettableCompletionSource;

        private ResettableCompletionSource<uint> _shutdownResettableCompletionSource;

        // Buffers to hold during a call to send.
        private readonly MemoryHandle[] _bufferArrays = new MemoryHandle[1];
        private readonly QuicBuffer[] _sendQuicBuffers = new QuicBuffer[1];

        // Handle to hold when sending.
        private GCHandle _sendHandle;

        // Used to check if StartAsync has been called.
        private StartState _started;

        private ReadState _readState;

        private ShutdownState _shutdownState;

        private SendState _sendState;

        // Used by the class to indicate that the stream is m_Readable.
        private bool _canRead;

        // Used by the class to indicate that the stream is writable.
        private bool _canWrite;

        private volatile bool _disposed = false;

        private List<QuicBuffer> _receiveQuicBuffers = new List<QuicBuffer>();

        private object _sync = new object();

        // Creates a new MsQuicStream
        internal MsQuicStream(MsQuicApi api, MsQuicConnection connection, QUIC_STREAM_OPEN_FLAG flags, IntPtr nativeObjPtr, bool inbound)
        {
            Debug.Assert(connection != null);

            _api = api;
            _ptr = nativeObjPtr;

            if (inbound)
            {
                _started = StartState.Finished;

                _canWrite = !flags.HasFlag(QUIC_STREAM_OPEN_FLAG.UNIDIRECTIONAL);
                _canRead = true;
            }
            else
            {
                _started = StartState.None;

                _canWrite = true;
                _canRead = !flags.HasFlag(QUIC_STREAM_OPEN_FLAG.UNIDIRECTIONAL);
            }

            _sendResettableCompletionSource = new ResettableCompletionSource<uint>();
            _receiveResettableCompletionSource = new ResettableCompletionSource<uint>();
            _shutdownResettableCompletionSource = new ResettableCompletionSource<uint>();

            SetCallbackHandler();
        }

        internal override bool CanRead => _canRead;

        internal override bool CanWrite => _canWrite;

        internal override long StreamId
        {
            get
            {
                if (_streamId == -1)
                {
                    _streamId = GetStreamId();
                }

                return _streamId;
            }
        }

        internal override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (NetEventSource.IsEnabled) NetEventSource.Enter(this);

            if (!_canWrite)
            {
                throw new InvalidOperationException("Writing is not allowed on stream.");
            }

            cancellationToken.Register(() =>
            {
                bool shouldComplete = false;
                lock (_sync)
                {
                    if (_sendState == SendState.None)
                    {
                        _sendState = SendState.Aborted;
                        shouldComplete = true;
                    }
                }

                if (shouldComplete)
                {
                    _sendResettableCompletionSource.CompleteException(new OperationCanceledException("Write was canceled"));
                }
            });

            if (_started == StartState.None)
            {
                _started = StartState.Started;
                await StartAsync();
            }

            await SendAsync(buffer, QUIC_SEND_FLAG.NONE);

            lock (_sync)
            {
                // TODO confirm the expected behavior when we cancel sending.
                // do we want to make it so write async always throws?
                if (_sendState == SendState.Finished)
                {
                    _sendState = SendState.None;
                }
            }

            if (NetEventSource.IsEnabled) NetEventSource.Exit(this);
        }

        internal override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            if (NetEventSource.IsEnabled) NetEventSource.Enter(this);
            if (!_canRead)
            {
                throw new InvalidOperationException("Reading is not allowed on stream.");
            }

            lock (_sync)
            {
                if (_readState == ReadState.ReadsCompleted)
                {
                    if (NetEventSource.IsEnabled) NetEventSource.Exit(this);
                    return 0;
                }
                else if (_readState == ReadState.Aborted)
                {
                    throw new IOException("Reading has been aborted by the peer.");
                }
            }

            cancellationToken.Register(() =>
            {
                bool shouldComplete = false;
                lock (_sync)
                {
                    if (_readState == ReadState.None)
                    {
                        shouldComplete = true;
                    }

                    _readState = ReadState.Aborted;
                }

                if (shouldComplete)
                {
                    _receiveResettableCompletionSource.CompleteException(new OperationCanceledException("Read was canceled"));
                }
            });

            // TODO there could potentially be a perf gain by storing the buffer from the inital read
            // This reduces the amount of async calls, however it makes it so MsQuic holds onto the buffers
            // longer than it needs to. We will need to benchmark this.
            int length = (int)await _receiveResettableCompletionSource.GetValueTask();

            static unsafe void CopyToBuffer(Span<byte> destinationBuffer, List<QuicBuffer> sourceBuffers)
            {
                Span<byte> slicedBuffer = destinationBuffer;
                for (int i = 0; i < sourceBuffers.Count; i++)
                {
                    QuicBuffer nativeBuffer = sourceBuffers[i];
                    int length = Math.Min((int)nativeBuffer.Length, slicedBuffer.Length);
                    new Span<byte>(nativeBuffer.Buffer, length).CopyTo(slicedBuffer);
                    if (length < slicedBuffer.Length)
                    {
                        return;
                    }
                    slicedBuffer = slicedBuffer.Slice(length);
                }
            }

            int actual = Math.Min(length, destination.Length);

            CopyToBuffer(destination.Span, _receiveQuicBuffers);

            lock (_sync)
            {
                if (_readState == ReadState.IndividualReadComplete)
                {
                    // Don't call receive complete after the stream has been aborted or completed.
                    EnableReceive();
                    ReceiveComplete(actual);
                    _readState = ReadState.None;
                }
            }

            if (NetEventSource.IsEnabled) NetEventSource.Exit(this);

            return actual;
        }

        // TODO do we want this to be a synchronization mechanism to cancel a pending read
        // If so, we need to complete the read here as well.
        internal override void AbortRead()
        {
            if (NetEventSource.IsEnabled) NetEventSource.Enter(this);

            lock (_sync)
            {
                _readState = ReadState.Aborted;
            }

            _api._streamShutdownDelegate(_ptr, (uint)QUIC_STREAM_SHUTDOWN_FLAG.ABORT_RECV, errorCode: 0);

            if (NetEventSource.IsEnabled) NetEventSource.Exit(this);
        }

        internal override ValueTask ShutdownWriteAsync(CancellationToken cancellationToken = default)
        {
            if (NetEventSource.IsEnabled) NetEventSource.Enter(this);

            // TODO do anything to stop writes?
            cancellationToken.Register(() =>
            {
                bool shouldComplete = false;
                lock (_sync)
                {
                    if (_shutdownState == ShutdownState.None)
                    {
                        _shutdownState = ShutdownState.Canceled;
                        shouldComplete = true;
                    }
                }

                if (shouldComplete)
                {
                    _shutdownResettableCompletionSource.CompleteException(new OperationCanceledException("Shutdown was canceled"));
                }
            });

            _api._streamShutdownDelegate(_ptr, (uint)QUIC_STREAM_SHUTDOWN_FLAG.GRACEFUL, errorCode: 0);

            if (NetEventSource.IsEnabled) NetEventSource.Exit(this);

            return _shutdownResettableCompletionSource.GetTypelessValueTask();
        }

        // TODO consider removing sync-over-async with blocking calls.
        internal override int Read(Span<byte> buffer)
        {
            return ReadAsync(buffer.ToArray()).GetAwaiter().GetResult();
        }

        internal override void Write(ReadOnlySpan<byte> buffer)
        {
            WriteAsync(buffer.ToArray()).GetAwaiter().GetResult();
        }

        // MsQuic doesn't support explicit flushing
        internal override void Flush()
        {
        }

        // MsQuic doesn't support explicit flushing
        internal override Task FlushAsync(CancellationToken cancellationToken = default)
        {
            return default;
        }

        public override ValueTask DisposeAsync()
        {
            if (NetEventSource.IsEnabled) NetEventSource.Enter(this);
            if (_disposed)
            {
                return default;
            }

            if (_ptr != IntPtr.Zero)
            {
                // TODO call shutdown here.
                //_api._streamShutdownDelegate(_ptr, (uint)QUIC_STREAM_SHUTDOWN_FLAG.ABORT, 1);
                _api._streamCloseDelegate?.Invoke(_ptr);
            }

            _handle.Free();
            _api = null;

            _disposed = true;

            return default;
        }

        public override void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~MsQuicStream()
        {
            Dispose(false);
        }

        // Synchronous shutdown current does a graceful shutdown, which must go async
        // Close can be done synchronously, but there is not guarantee that all data will be sent to the client
        // We probably need to reconsider how to handle dispose/shutdown cases.
        private void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (_ptr != IntPtr.Zero)
            {
                // TODO call shutdown here.
                //_api._streamShutdownDelegate(_ptr, (uint)QUIC_STREAM_SHUTDOWN_FLAG.ABORT, 1);
                _api._streamCloseDelegate?.Invoke(_ptr);
            }

            _handle.Free();
            _api = null;

            _disposed = true;
        }

        private void EnableReceive()
        {
            uint status = _api._streamReceiveSetEnabledDelegate(_ptr, enabled: true);
        }

        internal static uint NativeCallbackHandler(
           IntPtr stream,
           IntPtr context,
           StreamEvent connectionEventStruct)
        {
            var handle = GCHandle.FromIntPtr(context);
            var quicStream = (MsQuicStream)handle.Target;

            return quicStream.HandleEvent(ref connectionEventStruct);
        }

        private uint HandleEvent(ref StreamEvent evt)
        {
            uint status = MsQuicConstants.Success;

            try
            {
                switch (evt.Type)
                {
                    // Stream has started.
                    // Will only be done for outbound streams (inbound streams have already started)
                    case QUIC_STREAM_EVENT.START_COMPLETE:
                        status = HandleStartComplete();
                        break;
                    // Received data on the stream
                    case QUIC_STREAM_EVENT.RECEIVE:
                        {
                            status = HandleEventRecv(ref evt);
                        }
                        break;
                    // Send has completed.
                    // Contains a canceled bool to indicate if the send was canceled.
                    case QUIC_STREAM_EVENT.SEND_COMPLETE:
                        {
                            status = HandleEventSendComplete(ref evt);
                        }
                        break;
                    // Peer has told us to shutdown the reading side of the stream.
                    case QUIC_STREAM_EVENT.PEER_SEND_SHUTDOWN:
                        {
                            status = HandleEventPeerSendShutdown();
                        }
                        break;
                    // Peer has told us to abort the reading side of the stream.
                    case QUIC_STREAM_EVENT.PEER_SEND_ABORTED:
                        {
                            status = HandleEventPeerSendAborted();
                        }
                        break;
                    // Peer has stopped receiving data, don't send anymore.
                    // Potentially throw when WriteAsync/FlushAsync.
                    case QUIC_STREAM_EVENT.PEER_RECEIVE_ABORTED:
                        {
                            status = HandleEventPeerRecvAbort();
                        }
                        break;
                    // Occurs when shutdown is completed for the send side.
                    // This only happens for shutdown on sending, not receiving
                    // Receive shutdown can only be abortive.
                    case QUIC_STREAM_EVENT.SEND_SHUTDOWN_COMPLETE:
                        {
                            status = HandleEventSendShutdownComplete(ref evt);
                        }
                        break;
                    // Shutdown for both sending and receiving is completed.
                    case QUIC_STREAM_EVENT.SHUTDOWN_COMPLETE:
                        {
                            status = HandleEventShutdownComplete();
                        }
                        break;
                    default:
                        break;
                }
            }
            catch (Exception)
            {
                return MsQuicConstants.InternalError;
            }

            return status;
        }

        private unsafe uint HandleEventRecv(ref MsQuicNativeMethods.StreamEvent evt)
        {
            if (NetEventSource.IsEnabled) NetEventSource.Enter(this);

            StreamEventDataRecv receieveEvent = evt.Data.Recv;
            for (int i = 0; i < receieveEvent.BufferCount; i++)
            {
                _receiveQuicBuffers.Add(receieveEvent.Buffers[i]);
            }

            bool shouldComplete = false;
            lock (_sync)
            {
                if (_readState == ReadState.None)
                {
                    shouldComplete = true;
                }
                _readState = ReadState.IndividualReadComplete;
            }

            if (shouldComplete)
            {
                _receiveResettableCompletionSource.Complete((uint)receieveEvent.TotalBufferLength);
            }

            if (NetEventSource.IsEnabled) NetEventSource.Exit(this);

            return MsQuicConstants.Pending;
        }

        private uint HandleEventPeerRecvAbort()
        {
            if (NetEventSource.IsEnabled) NetEventSource.Enter(this);
            // TODO
            if (NetEventSource.IsEnabled) NetEventSource.Exit(this);

            return MsQuicConstants.Success;
        }

        private uint HandleStartComplete()
        {
            if (NetEventSource.IsEnabled) NetEventSource.Enter(this);

            bool shouldComplete = false;
            lock (_sync)
            {
                _started = StartState.Finished;

                // Check send state before completing as send cancellation is shared between start and send.
                if (_sendState == SendState.None)
                {
                    shouldComplete = true;
                }
            }

            if (shouldComplete)
            {
                _sendResettableCompletionSource.Complete(MsQuicConstants.Success);
            }

            if (NetEventSource.IsEnabled) NetEventSource.Exit(this);

            return MsQuicConstants.Success;
        }

        private uint HandleEventSendShutdownComplete(ref MsQuicNativeMethods.StreamEvent evt)
        {
            if (NetEventSource.IsEnabled) NetEventSource.Enter(this);

            _shutdownResettableCompletionSource.Complete(MsQuicConstants.Success);

            if (NetEventSource.IsEnabled) NetEventSource.Exit(this);

            return MsQuicConstants.Success;
        }

        private uint HandleEventShutdownComplete()
        {
            if (NetEventSource.IsEnabled) NetEventSource.Enter(this);

            // TODO use another cts here? This is when both sides are shutdown.
            // IDK if there is anything useful to do here.

            if (NetEventSource.IsEnabled) NetEventSource.Exit(this);

            return MsQuicConstants.Success;
        }

        private uint HandleEventPeerSendAborted()
        {
            if (NetEventSource.IsEnabled) NetEventSource.Enter(this);

            bool shouldComplete = false;
            lock (_sync)
            {
                if (_readState == ReadState.None)
                {
                    shouldComplete = true;
                }
                _readState = ReadState.Aborted;
            }

            if (shouldComplete)
            {
                _receiveResettableCompletionSource.CompleteException(new IOException("Reading has been aborted by the peer."));
            }

            if (NetEventSource.IsEnabled) NetEventSource.Exit(this);

            return MsQuicConstants.Success;
        }

        private uint HandleEventPeerSendShutdown()
        {
            if (NetEventSource.IsEnabled) NetEventSource.Enter(this);

            bool shouldComplete = false;

            lock (_sync)
            {
                // This event won't occur within the middle of a receive.
                if (NetEventSource.IsEnabled) NetEventSource.Info("Completing resettable event source.");

                if (_readState == ReadState.None)
                {
                    shouldComplete = true;
                }

                _readState = ReadState.ReadsCompleted;
            }

            if (shouldComplete)
            {
                _receiveResettableCompletionSource.Complete(0);
            }

            if (NetEventSource.IsEnabled) NetEventSource.Exit(this);

            return MsQuicConstants.Success;
        }

        private uint HandleEventSendComplete(ref StreamEvent evt)
        {
            if (NetEventSource.IsEnabled) NetEventSource.Enter(this);

            CleanupSendState();
            // TODO throw if a write failed?
            uint errorCode = evt.Data.SendComplete.Canceled;

            bool shouldComplete = false;
            lock (_sync)
            {
                if (_sendState == SendState.None)
                {
                    _sendState = SendState.Finished;
                    shouldComplete = true;
                }
            }

            if (shouldComplete)
            {
                _sendResettableCompletionSource.Complete(MsQuicConstants.Success);
            }

            if (NetEventSource.IsEnabled) NetEventSource.Exit(this);

            return MsQuicConstants.Success;
        }

        private void CleanupSendState()
        {
            _sendHandle.Free();
            _bufferArrays[0].Dispose();
        }

        private void SetCallbackHandler()
        {
            _handle = GCHandle.Alloc(this);

            _callback = new StreamCallbackDelegate(NativeCallbackHandler);
            _api._setCallbackHandlerDelegate(
                _ptr,
                _callback,
                GCHandle.ToIntPtr(_handle));
        }

        // TODO prevent overlapping sends.
        // TODO consider allowing overlapped reads.
        internal unsafe ValueTask SendAsync(
           ReadOnlyMemory<byte> buffer,
           QUIC_SEND_FLAG flags)
        {
            MemoryHandle handle = buffer.Pin();
            _sendQuicBuffers[0].Length = (uint)buffer.Length;
            _sendQuicBuffers[0].Buffer = (byte*)handle.Pointer;

            _bufferArrays[0] = handle;

            _sendHandle = GCHandle.Alloc(_sendQuicBuffers, GCHandleType.Pinned);

            var quicBufferPointer = (QuicBuffer*)Marshal.UnsafeAddrOfPinnedArrayElement(_sendQuicBuffers, 0);

            uint status = _api._streamSendDelegate(
                _ptr,
                quicBufferPointer,
                bufferCount: 1,
                (uint)flags,
                _ptr);

            if (!MsQuicStatusHelper.SuccessfulStatusCode(status))
            {
                CleanupSendState();
                MsQuicStatusException.ThrowIfFailed(status);
            }

            return _sendResettableCompletionSource.GetTypelessValueTask();
        }

        private ValueTask<uint> StartAsync()
        {
            uint status = _api._streamStartDelegate(
              _ptr,
              (uint)QUIC_STREAM_START_FLAG.ASYNC);

            MsQuicStatusException.ThrowIfFailed(status);
            return _sendResettableCompletionSource.GetValueTask();
        }

        private void ReceiveComplete(int bufferLength)
        {
            uint status = _api._streamReceiveCompleteDelegate(_ptr, (ulong)bufferLength);
            MsQuicStatusException.ThrowIfFailed(status);
        }

        // This can fail if the stream isn't started.
        private unsafe long GetStreamId()
        {
            byte* ptr = stackalloc byte[sizeof(long)];
            QuicBuffer buffer = new QuicBuffer
            {
                Length = sizeof(long),
                Buffer = ptr
            };

            MsQuicStatusException.ThrowIfFailed(_api.UnsafeGetParam(
                _ptr,
                (uint)QUIC_PARAM_LEVEL.STREAM,
                (uint)QUIC_PARAM_STREAM.ID,
                ref buffer));
            return *(long*)ptr;
        }

        private enum StartState
        {
            None,
            Started,
            Finished
        }

        private enum ReadState
        {
            None,
            IndividualReadComplete,
            ReadsCompleted,
            Aborted
        }

        private enum ShutdownState
        {
            None,
            Canceled,
            Finished
        }

        private enum SendState
        {
            None,
            Aborted,
            Finished
        }
    }
}
