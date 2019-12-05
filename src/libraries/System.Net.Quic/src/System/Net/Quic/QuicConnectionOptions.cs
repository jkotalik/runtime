﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net.Security;

namespace System.Net.Quic
{
    internal class QuicConnectionOptions
    {
        public SslClientAuthenticationOptions ServerAuthenticationOptions { get; set; }
        public IPEndPoint ListenEndpoint { get; set; }
        public int BidirectionalStreamCount { get; set; } = 100;
        public int UnidirectionalStreamCount { get; set; } = 100;
    }
}