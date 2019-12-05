﻿using System.Collections.Generic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace System.Net.Quic.Tests
{
    public class MsQuicTestBase : IDisposable
    {
        public MsQuicTestBase()
        {
            DefaultEndpoint = new IPEndPoint(IPAddress.Loopback, 8000);
            DefaultListener = CreateQuicListener(DefaultEndpoint);
        }

        public IPEndPoint DefaultEndpoint { get; }
        public QuicListener DefaultListener { get; }

        public SslServerAuthenticationOptions GetSslServerAuthenticationOptions()
        {
            // TODO figure out how to supply a fake cert here.
            using (X509Store store = new X509Store(StoreLocation.LocalMachine))
            {
                store.Open(OpenFlags.ReadOnly);
                X509Certificate2Collection cers = store.Certificates.Find(X509FindType.FindBySubjectName, "localhost", false);
                return new SslServerAuthenticationOptions()
                {
                    ServerCertificate = cers[0],
                    ApplicationProtocols = new List<SslApplicationProtocol>() { new SslApplicationProtocol("quictest") }
                };
            }
        }

        public SslClientAuthenticationOptions GetSslClientAuthenticationOptions()
        {
            return new SslClientAuthenticationOptions()
            {
                ApplicationProtocols = new List<SslApplicationProtocol>() { new SslApplicationProtocol("quictest") }
            };
        }

        public QuicConnection CreateQuicConnection(IPEndPoint endpoint)
        {
            return new QuicConnection(QuicImplementationProviders.MsQuic, endpoint, GetSslClientAuthenticationOptions());
        }

        public QuicListener CreateQuicListener(IPEndPoint endpoint)
        {
            return new QuicListener(QuicImplementationProviders.MsQuic, endpoint, GetSslServerAuthenticationOptions());
        }

        public void Dispose()
        {
            DefaultListener.Dispose();
        }
    }
}