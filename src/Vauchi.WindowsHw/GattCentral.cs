// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Vauchi.Contract;

namespace Vauchi.WindowsHw;

/// <summary>One characteristic of the peer, its UUID in the canonical lower-case form.</summary>
public sealed record GattCharacteristic(string Uuid, bool Notifies);

/// <summary>
/// What the OS tells about a link after it is up. An implementation calls
/// these from whatever thread the OS uses; none of them blocks.
/// </summary>
public interface IGattSignals
{
    void Notified(string uuid, byte[] data);

    void MtuChanged(uint mtu);

    void Lost();
}

/// <summary>
/// A connected peer with its services resolved. Disposal disconnects.
/// Each call answers whether the OS did it; none of them throws for a
/// refusal by the peer.
/// </summary>
public interface IGattLink : IAsyncDisposable
{
    /// <summary>The ATT MTU, when the OS names one.</summary>
    uint? Mtu { get; }

    IReadOnlyList<GattCharacteristic> Characteristics { get; }

    Task<bool> SubscribeAsync(string uuid, CancellationToken stop);

    Task<bool> WriteAsync(string uuid, byte[] data, CancellationToken stop);

    Task<byte[]?> ReadAsync(string uuid, CancellationToken stop);
}

/// <summary>
/// The OS call that reaches a peer by address. It throws
/// <see cref="HardwareFailureException"/> when the OS says why it cannot.
/// </summary>
public interface IGattDialer
{
    Task<IGattLink> DialAsync(ulong address, IGattSignals signals, CancellationToken stop);
}

/// <summary>
/// The central port over a GATT link, with the events and their order of
/// <c>linux-hw</c>'s central (<c>src/bluez/central.rs</c>): the MTU when
/// it is known, then the connection once every notifying characteristic
/// is subscribed.
/// </summary>
public sealed class GattCentral(IGattDialer dialer) : ICentral
{
    /// <summary>A Bluetooth device address is 48 bits.</summary>
    private const ulong MaxAddress = (1UL << 48) - 1;

    private const int MaxAddressDigits = 15;

    /// <summary>The longest attribute value ATT carries; a backend copies nothing longer.</summary>
    public const int MaxAttributeValue = BleBounds.MaxValue;

    public async Task ConnectAsync(
        string deviceId,
        ChannelReader<LinkRequest> requests,
        EventSink events,
        CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(events);
        if (!TryAddress(deviceId, out ulong address))
        {
            throw new HardwareFailureException(HardwareFailure.Failed);
        }
        var signals = new Signals();
        IGattLink link = await dialer.DialAsync(address, signals, stop).ConfigureAwait(false);
        await using (link.ConfigureAwait(false))
        {
            await new Link(deviceId, link, events, stop).RunAsync(requests, signals.Reader).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The decimal form <see cref="BleObservations.Observed"/> hands out,
    /// and no other spelling: a lenient parse would dial a device Core did
    /// not name.
    /// </summary>
    internal static bool TryAddress(string deviceId, out ulong address)
    {
        address = 0;
        if (deviceId is not { Length: > 0 and <= MaxAddressDigits } || (deviceId[0] == '0' && deviceId.Length > 1))
        {
            return false;
        }
        foreach (char digit in deviceId)
        {
            if (digit is < '0' or > '9')
            {
                return false;
            }
        }
        address = ulong.Parse(deviceId, NumberStyles.None, CultureInfo.InvariantCulture);
        return address <= MaxAddress;
    }

    private abstract record Signal
    {
        private Signal()
        {
        }

        internal sealed record Notified(string Uuid, byte[] Data) : Signal;

        internal sealed record Mtu(uint Value) : Signal;

        internal sealed record Lost : Signal;
    }

    /// <summary>
    /// What the OS reported and the link has not passed on yet. A chunked
    /// exchange cannot lose a notification, so a peer that fills this ends
    /// the link with a failure instead of having one dropped.
    /// </summary>
    private sealed class Signals : IGattSignals
    {
        private readonly Channel<Signal> _waiting = Channel.CreateBounded<Signal>(
            new BoundedChannelOptions(BleBounds.MaxQueued) { SingleReader = true });

        internal ChannelReader<Signal> Reader => _waiting.Reader;

        public void Notified(string uuid, byte[] data)
        {
            if (uuid is { Length: <= BleBounds.MaxUuid } && data is { Length: <= BleBounds.MaxValue })
            {
                Keep(new Signal.Notified(uuid, data));
            }
        }

        public void MtuChanged(uint mtu) => Keep(new Signal.Mtu(mtu));

        public void Lost()
        {
            Keep(new Signal.Lost());
            _waiting.Writer.TryComplete();
        }

        private void Keep(Signal signal)
        {
            if (!_waiting.Writer.TryWrite(signal))
            {
                _waiting.Writer.TryComplete(new HardwareFailureException(HardwareFailure.Failed));
            }
        }
    }

    private sealed class Link
    {
        private readonly string _deviceId;
        private readonly IGattLink _link;
        private readonly EventSink _events;
        private readonly CancellationToken _stop;
        private readonly Dictionary<string, string> _characteristics = new(StringComparer.Ordinal);

        internal Link(string deviceId, IGattLink link, EventSink events, CancellationToken stop)
        {
            _deviceId = deviceId;
            _link = link;
            _events = events;
            _stop = stop;
        }

        internal async Task RunAsync(ChannelReader<LinkRequest> requests, ChannelReader<Signal> signals)
        {
            foreach (GattCharacteristic characteristic in _link.Characteristics)
            {
                _characteristics[characteristic.Uuid.ToLowerInvariant()] = characteristic.Uuid;
                if (characteristic.Notifies
                    && !await _link.SubscribeAsync(characteristic.Uuid, _stop).ConfigureAwait(false))
                {
                    throw new HardwareFailureException(HardwareFailure.Failed);
                }
            }
            if (_link.Mtu is { } mtu && !await Report(Negotiated(mtu)).ConfigureAwait(false))
            {
                return;
            }
            var connected = new Event.BleConnected { DeviceId = _deviceId, Direction = BleLinkDirection.Outbound };
            if (!await Report(connected).ConfigureAwait(false))
            {
                return;
            }
            Task<bool>? request = null;
            Task<bool>? signal = null;
            while (true)
            {
                request ??= requests.WaitToReadAsync(_stop).AsTask();
                signal ??= signals.WaitToReadAsync(_stop).AsTask();
                Task<bool> ready = await Task.WhenAny(request, signal).ConfigureAwait(false);
                if (ready == signal)
                {
                    signal = null;
                    // Awaiting it is what raises the failure of a link that overflowed.
                    if (!await ready.ConfigureAwait(false)
                        || !await Observe(signals).ConfigureAwait(false))
                    {
                        return;
                    }
                    continue;
                }
                request = null;
                if (!await ready.ConfigureAwait(false) || !await Serve(requests).ConfigureAwait(false))
                {
                    return;
                }
            }
        }

        private async Task<bool> Observe(ChannelReader<Signal> signals)
        {
            while (signals.TryRead(out Signal? signal))
            {
                switch (signal)
                {
                    case Signal.Notified notified:
                        var reported = new Event.BleCharacteristicNotified
                        {
                            DeviceId = _deviceId,
                            Direction = BleLinkDirection.Outbound,
                            Uuid = notified.Uuid,
                            Data = notified.Data,
                        };
                        if (!await Report(reported).ConfigureAwait(false))
                        {
                            return false;
                        }
                        break;
                    case Signal.Mtu mtu:
                        if (!await Report(Negotiated(mtu.Value)).ConfigureAwait(false))
                        {
                            return false;
                        }
                        break;
                    default:
                        await Report(new Event.BleDisconnected
                        {
                            DeviceId = _deviceId,
                            Direction = BleLinkDirection.Outbound,
                            Reason = "the peer disconnected",
                        }).ConfigureAwait(false);
                        return false;
                }
            }
            return true;
        }

        private async Task<bool> Serve(ChannelReader<LinkRequest> requests)
        {
            while (requests.TryRead(out LinkRequest? request))
            {
                Event? answer = request switch
                {
                    LinkRequest.Write write => await Write(write).ConfigureAwait(false),
                    LinkRequest.Read read => await Read(read).ConfigureAwait(false),
                    _ => null,
                };
                if (answer is not null && !await Report(answer).ConfigureAwait(false))
                {
                    return false;
                }
            }
            return true;
        }

        private async Task<Event?> Write(LinkRequest.Write write)
        {
            if (!_characteristics.TryGetValue(write.Uuid.ToLowerInvariant(), out string? uuid))
            {
                return Failure("Bluetooth characteristic not found");
            }
            return await _link.WriteAsync(uuid, write.Data, _stop).ConfigureAwait(false)
                ? null
                : Failure("Bluetooth write failed");
        }

        private async Task<Event?> Read(LinkRequest.Read read)
        {
            byte[]? data = _characteristics.TryGetValue(read.Uuid.ToLowerInvariant(), out string? uuid)
                ? await _link.ReadAsync(uuid, _stop).ConfigureAwait(false)
                : null;
            if (data is null)
            {
                return Failure("Bluetooth read failed");
            }
            return new Event.BleCharacteristicRead
            {
                DeviceId = _deviceId,
                Direction = BleLinkDirection.Outbound,
                Uuid = read.Uuid,
                Data = data,
            };
        }

        private Event Negotiated(uint mtu) => new Event.BleMtuNegotiated { DeviceId = _deviceId, Mtu = mtu };

        private static Event Failure(string error) => new Event.HardwareError { Transport = "BLE", Error = error };

        /// <summary>False once Core no longer takes this link's events.</summary>
        private async Task<bool> Report(Event observation) =>
            await _events.SendAsync(observation, _stop).ConfigureAwait(false) != SendOutcome.Ended;
    }
}
