﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net.Security;
using System.Text;

namespace System.Net.Quic.Implementations.MsQuic.Internal
{
    internal sealed class MsQuicSession : IDisposable
    {
        private bool _disposed = false;
        private IntPtr _nativeObjPtr;
        private MsQuicApi _api;
        private bool _opened;

        internal MsQuicSession(string registration)
        {
            _api = MsQuicApi.Api;
        }

        public MsQuicConnection ConnectionOpen(IPEndPoint endpoint, SslClientAuthenticationOptions sslClientAuthenticationOptions, IPEndPoint localEndpoint)
        {
            if (!_opened)
            {
                _nativeObjPtr = _api.SessionOpen(sslClientAuthenticationOptions.ApplicationProtocols[0].Protocol.ToArray());
                _opened = true;
                SetPeerBiDirectionalStreamCount(100); // TODO make these configurable.
                SetPeerUnidirectionalStreamCount(100);
            }

            uint status = _api._connectionOpenDelegate(
                _nativeObjPtr,
                MsQuicConnection.NativeCallbackHandler,
                IntPtr.Zero,
                out IntPtr connectionPtr);

            MsQuicStatusException.ThrowIfFailed(status);

            return new MsQuicConnection(endpoint, _api, connectionPtr, sslClientAuthenticationOptions);
        }

        // TODO allow for a callback to select the certificate (SNI).
        public MsQuicListener ListenerOpen(IPEndPoint listenEndPoint, SslServerAuthenticationOptions sslServerAuthenticationOptions)
        {
            if (!_opened)
            {
                _nativeObjPtr = _api.SessionOpen(sslServerAuthenticationOptions.ApplicationProtocols[0].Protocol.ToArray());
                _opened = true;
                // TODO figure out a story for configuration.
                SetPeerBiDirectionalStreamCount(100);
                SetPeerUnidirectionalStreamCount(100);
            }

            uint status = _api._listenerOpenDelegate(
                _nativeObjPtr,
                MsQuicListener.NativeCallbackHandler,
                IntPtr.Zero,
                out IntPtr listenerPointer
                );

            var listener = new MsQuicListener(listenEndPoint, sslServerAuthenticationOptions, _api, listenerPointer);

            return listener;
        }

        // TODO call this for graceful shutdown.
        public void ShutDown(
            QUIC_CONNECTION_SHUTDOWN_FLAG Flags,
            ushort ErrorCode)
        {
            _api._sessionShutdownDelegate(
                _nativeObjPtr,
                (uint)Flags,
                ErrorCode);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public void SetPeerBiDirectionalStreamCount(ushort count)
        {
            SetUshortParamter(QUIC_PARAM_SESSION.PEER_BIDI_STREAM_COUNT, count);
        }

        public void SetPeerUnidirectionalStreamCount(ushort count)
        {
            SetUshortParamter(QUIC_PARAM_SESSION.PEER_UNIDI_STREAM_COUNT, count);
        }

        private unsafe void SetUshortParamter(QUIC_PARAM_SESSION param, ushort count)
        {
            var buffer = new MsQuicNativeMethods.QuicBuffer()
            {
                Length = sizeof(ushort),
                Buffer = (byte*)&count
            };

            SetParam(param, buffer);
        }

        public void SetDisconnectTimeout(TimeSpan timeout)
        {
            SetULongParamter(QUIC_PARAM_SESSION.DISCONNECT_TIMEOUT, (ulong)timeout.TotalMilliseconds);
        }

        public void SetIdleTimeout(TimeSpan timeout)
        {
            SetULongParamter(QUIC_PARAM_SESSION.IDLE_TIMEOUT, (ulong)timeout.TotalMilliseconds);

        }
        private unsafe void SetULongParamter(QUIC_PARAM_SESSION param, ulong count)
        {
            var buffer = new MsQuicNativeMethods.QuicBuffer()
            {
                Length = sizeof(ulong),
                Buffer = (byte*)&count
            };
            SetParam(param, buffer);
        }

        private void SetParam(
          QUIC_PARAM_SESSION param,
          MsQuicNativeMethods.QuicBuffer buf)
        {
            MsQuicStatusException.ThrowIfFailed(_api.UnsafeSetParam(
                _nativeObjPtr,
                (uint)QUIC_PARAM_LEVEL.SESSION,
                (uint)param,
                buf));
        }

        ~MsQuicSession()
        {
            Dispose(false);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            _api._sessionCloseDelegate?.Invoke(_nativeObjPtr);
            _nativeObjPtr = IntPtr.Zero;
            _api = null;

            _disposed = true;
        }
    }
}
