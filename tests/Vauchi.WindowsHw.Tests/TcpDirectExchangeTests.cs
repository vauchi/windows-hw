// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Vauchi.Contract;
using Xunit;

namespace Vauchi.WindowsHw.Tests;

/// <summary>Against a real peer on the loopback interface: the socket is the port's boundary.</summary>
public class TcpDirectExchangeTests
{
    private static DirectEndpoint Endpoint(int port, string host = "127.0.0.1", uint timeoutMs = 20_000) => new()
    {
        FallbackHost = host,
        Port = (ushort)port,
        IoTimeoutMs = timeoutMs,
        BrowseTimeoutMs = 0,
        ServiceType = "_vauchi-exchange._tcp.local.",
    };

    /// <summary>A peer that accepts one connection and runs <paramref name="serve"/> on it.</summary>
    private static (int Port, Task Served) Peer(Action<NetworkStream> serve)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task served = Task.Run(async () =>
        {
            try
            {
                using TcpClient connection = await listener.AcceptTcpClientAsync();
                serve(connection.GetStream());
            }
            finally
            {
                listener.Stop();
            }
        });
        return (port, served);
    }

    private static string Refusal(Action act) => Assert.Throws<DirectExchangeException>(act).Message;

    [Fact]
    public async Task TheInitiatorSendsItsFrameFirstAndReturnsThePeersPayload()
    {
        byte[]? received = null;
        (int port, Task served) = Peer(link =>
        {
            received = DirectFrame.Read(link);
            DirectFrame.Write(link, new byte[] { 9, 9 });
        });

        byte[] theirs = new TcpDirectExchange().Exchange(Endpoint(port), new byte[] { 1, 2, 3 }, isInitiator: true, default);

        await served.WaitAsync(Wait.Limit);
        Assert.Equal(new byte[] { 9, 9 }, theirs);
        Assert.Equal(new byte[] { 1, 2, 3 }, received);
    }

    [Fact]
    public async Task TheResponderReadsThePeersFrameBeforeItSendsItsOwn()
    {
        byte[]? received = null;
        (int port, Task served) = Peer(link =>
        {
            DirectFrame.Write(link, new byte[] { 7 });
            received = DirectFrame.Read(link);
        });

        byte[] theirs = new TcpDirectExchange().Exchange(Endpoint(port), new byte[] { 4, 5 }, isInitiator: false, default);

        await served.WaitAsync(Wait.Limit);
        Assert.Equal(new byte[] { 7 }, theirs);
        Assert.Equal(new byte[] { 4, 5 }, received);
    }

    [Theory]
    [InlineData("phone.local")]
    [InlineData("")]
    [InlineData("127.0.0.1; rm")]
    [InlineData("999.1.1.1")]
    public void AFallbackHostThatIsNotAnAddressIsRefusedWithoutALookup(string host)
    {
        Assert.Equal(
            "bad address",
            Refusal(() => new TcpDirectExchange().Exchange(Endpoint(1, host), new byte[] { 1 }, true, default)));
    }

    [Fact]
    public void APortNobodyListensOnIsAConnectFailureNamedByItsCodeNotItsAddress()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int closed = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        string refusal = Refusal(() => new TcpDirectExchange().Exchange(Endpoint(closed), new byte[] { 1 }, true, default));

        Assert.Equal("TCP connect failed: ConnectionRefused", refusal);
    }

    [Fact]
    public async Task APeerThatSendsAnotherProtocolIsRefusedByTheFramesRule()
    {
        (int port, Task served) = Peer(link =>
        {
            link.Write("HTTP/1.1 400 Bad Request\r\n\r\n"u8);
            link.Flush();
        });

        string refusal = Refusal(() => new TcpDirectExchange().Exchange(Endpoint(port), new byte[] { 1 }, false, default));

        await served.WaitAsync(Wait.Limit);
        Assert.Equal("exchange failed: invalid frame magic", refusal);
    }

    [Fact]
    public async Task APeerThatAnnouncesMoreThanTheBoundIsRefusedBeforeItsPayloadIsRead()
    {
        (int port, Task served) = Peer(link =>
        {
            link.Write(new byte[] { (byte)'V', (byte)'X', (byte)'C', (byte)'H', 1, 0xFF, 0xFF, 0xFF, 0xFF });
            link.Flush();
        });

        string refusal = Refusal(() => new TcpDirectExchange().Exchange(Endpoint(port), new byte[] { 1 }, false, default));

        await served.WaitAsync(Wait.Limit);
        Assert.Equal("exchange failed: payload too large", refusal);
    }

    [Fact]
    public async Task APeerThatClosesInsideAFrameIsRefusedAsEndedEarly()
    {
        (int port, Task served) = Peer(link =>
        {
            link.Write(new byte[] { (byte)'V', (byte)'X', (byte)'C', (byte)'H', 1, 0, 0, 0, 5, 1, 2 });
            link.Flush();
        });

        string refusal = Refusal(() => new TcpDirectExchange().Exchange(Endpoint(port), new byte[] { 1 }, false, default));

        await served.WaitAsync(Wait.Limit);
        Assert.Equal("exchange failed: frame ended early", refusal);
    }

    [Fact]
    public async Task StoppingEndsAnExchangeThatWaitsForThePeer()
    {
        using var stop = new CancellationTokenSource();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        (int port, Task served) = Peer(_ =>
        {
            connected.TrySetResult();
            release.Wait(Wait.Limit);
        });
        Task<byte[]> exchange = Task.Run(
            () => new TcpDirectExchange().Exchange(Endpoint(port), new byte[] { 1 }, isInitiator: false, stop.Token));
        await connected.Task.WaitAsync(Wait.Limit);

        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exchange.WaitAsync(Wait.Limit));
        release.Set();
        await served.WaitAsync(Wait.Limit);
    }

    [Fact]
    public async Task AnExchangeThroughTheShellReportsThePeersPayload()
    {
        (int port, Task served) = Peer(link =>
        {
            DirectFrame.Read(link);
            DirectFrame.Write(link, new byte[] { 8 });
        });
        await using var shell = new HardwareShell(new Backends { Direct = new TcpDirectExchange() }, new DeviceCapabilities());

        shell.Execute(new Command.DirectSend { Payload = new byte[] { 1 }, IsInitiator = true, Endpoint = Endpoint(port) });

        Assert.Equal("{\"DirectPayloadReceived\":{\"data\":[8]}}", (await shell.Next()).Wire());
        await served.WaitAsync(Wait.Limit);
    }
}
