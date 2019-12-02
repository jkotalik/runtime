﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace System.Net.Quic.Tests
{
    public class MsQuicTests : MsQuicTestBase
    {
        private static ReadOnlyMemory<byte> s_data = Encoding.UTF8.GetBytes("Hello world!");

        [Fact(Skip = "Unskip when MsQuic depenency is resolved")]
        public async Task BasicTest()
        {
            Task listenTask = Task.Run(async () =>
            {
                // Right now, AcceptConnections is required to start before staring a client connection.
                // We can try to fix this by either 
                using (QuicConnection connection = await DefaultListener.AcceptConnectionAsync())
                {
                    using (QuicStream stream = await connection.AcceptStreamAsync())
                    {
                        byte[] buffer = new byte[s_data.Length];
                        int bytesRead = await stream.ReadAsync(buffer);
                        Assert.Equal(s_data.Length, bytesRead);
                        Assert.True(s_data.Span.SequenceEqual(buffer));
                        await stream.WriteAsync(s_data);
                    }
                }
            });

            Task clientTask = Task.Run(async () =>
            {
                await using (QuicConnection connection = CreateQuicConnection(DefaultEndpoint))
                {
                    await connection.ConnectAsync();
                    using (QuicStream stream = connection.OpenBidirectionalStream())
                    {
                        await stream.WriteAsync(s_data);
                        byte[] memory = new byte[12];
                        int res = await stream.ReadAsync(memory);
                        Assert.True(s_data.Span.SequenceEqual(memory));
                    }
                }
            });

            await Task.WhenAll(listenTask, clientTask);
        }

        [Fact(Skip = "Unskip when MsQuic depenency is resolved")]
        public async Task MultipleReadsAndWrites()
        {
            Task listenTask = Task.Run(async () =>
            {
                // Right now, AcceptConnections is required to start before staring a client connection.
                // We can try to fix this by either 
                using (QuicConnection connection = await DefaultListener.AcceptConnectionAsync())
                {
                    using (QuicStream stream = await connection.AcceptStreamAsync())
                    {
                        byte[] buffer = new byte[s_data.Length];
                        while (true)
                        {
                            int bytesRead = await stream.ReadAsync(buffer);
                            if (bytesRead == 0)
                            {
                                break;
                            }
                            Assert.Equal(s_data.Length, bytesRead);
                            Assert.True(s_data.Span.SequenceEqual(buffer));
                        }
                        for (int i = 0; i < 100; i++)
                        {
                            await stream.WriteAsync(s_data);
                        }
                    }
                }
            });

            Task clientTask = Task.Run(async () =>
            {
                await using (QuicConnection connection = CreateQuicConnection(DefaultEndpoint))
                {
                    await connection.ConnectAsync();
                    using (QuicStream stream = connection.OpenBidirectionalStream())
                    {
                        for (int i = 0; i < 100; i++)
                        {
                            await stream.WriteAsync(s_data);
                        }

                        stream.ShutdownWrite();

                        byte[] memory = new byte[12];
                        while (true)
                        {
                            int res = await stream.ReadAsync(memory);
                            if (res == 0)
                            {
                                break;
                            }
                            Assert.True(s_data.Span.SequenceEqual(memory));
                        }
                    }
                }
            });

            await Task.WhenAll(listenTask, clientTask);
        }
    }
}
