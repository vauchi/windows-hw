// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Vauchi.Contract;

namespace Vauchi.WindowsHw;

/// <summary>
/// <c>DirectSend</c>: one exchange with the peer at Core's endpoint. The
/// peer's payload is reported as <c>DirectPayloadReceived</c>, a failure as
/// a <c>HardwareError</c> on "USB", the transport the display shells
/// always named.
/// </summary>
internal sealed class DirectSession(PortSession session, IDirectExchange direct)
{
    internal const string Transport = "USB";
    internal const string Failure = "direct exchange failed";

    internal PortSession Session { get; } = session;

    internal DeliveryError? Send(byte[] payload, bool isInitiator, DirectEndpoint endpoint)
    {
        if (payload.Length == 0 || payload.Length > DirectFrame.MaxPayload)
        {
            return DeliveryError.PayloadTooLarge;
        }
        return Session.Start(async (events, stop) =>
        {
            Event outcome;
            try
            {
                // The port blocks; the session's task is a pool thread it may hold.
                outcome = new Event.DirectPayloadReceived { Data = direct.Exchange(endpoint, payload, isInitiator, stop) };
            }
            catch (DirectExchangeException failure)
            {
                outcome = new Event.HardwareError { Transport = Transport, Error = failure.Message };
            }
            await events.SendAsync(outcome, stop).ConfigureAwait(false);
        });
    }
}

/// <summary>
/// TCP to the phone on the USB tether or the local network, at Core's
/// fallback host and port, one frame each way with the contract's codec.
/// </summary>
public sealed class TcpDirectExchange : IDirectExchange
{
    public byte[] Exchange(DirectEndpoint endpoint, byte[] payload, bool isInitiator, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(payload);
        // An address, never a name: resolving one would send Core's
        // fallback host to whatever answers DNS on this network.
        if (!IPAddress.TryParse(endpoint.FallbackHost, out IPAddress? host))
        {
            throw new DirectExchangeException("bad address");
        }
        int timeout = checked((int)Math.Min(endpoint.IoTimeoutMs, int.MaxValue));
        using var client = new TcpClient(host.AddressFamily);
        // Closing the socket is what ends a blocking read when Core stops the exchange.
        using CancellationTokenRegistration closing = stop.Register(client.Close);
        try
        {
            if (!client.ConnectAsync(host, endpoint.Port).Wait(timeout, stop))
            {
                throw new DirectExchangeException("TCP connect failed: TimedOut");
            }
            client.ReceiveTimeout = timeout;
            client.SendTimeout = timeout;
            return DirectFrame.Exchange(client.GetStream(), payload, isInitiator);
        }
        catch (DirectFrameException refused)
        {
            throw new DirectExchangeException($"exchange failed: {refused.Message}");
        }
        catch (Exception) when (stop.IsCancellationRequested)
        {
            throw new OperationCanceledException(stop);
        }
        catch (AggregateException failed) when (failed.InnerException is SocketException socket)
        {
            throw new DirectExchangeException($"TCP connect failed: {socket.SocketErrorCode}");
        }
        catch (IOException failed)
        {
            // The OS text of a socket error can hold the peer's address; its code does not.
            string code = failed.InnerException is SocketException socket ? socket.SocketErrorCode.ToString() : "Io";
            throw new DirectExchangeException($"exchange failed: {code}");
        }
        catch (SocketException failed)
        {
            throw new DirectExchangeException($"exchange failed: {failed.SocketErrorCode}");
        }
    }
}
