// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Vauchi.Contract;

namespace Vauchi.WindowsHw;

/// <summary>What a central may do with a characteristic.</summary>
[Flags]
public enum GattAccess
{
    None = 0,
    Read = 1,
    Write = 2,
    WriteWithoutResponse = 4,
    Notify = 8,
}

/// <summary>One characteristic of the exchange's service, its UUID in the canonical lower-case form.</summary>
public sealed record GattServerCharacteristic(string Uuid, GattAccess Access);

/// <summary>
/// The service to publish and the two service UUIDs to advertise. Nothing
/// else is advertised: no local name, so the advertisement carries nothing
/// that names the user or the device.
/// </summary>
public sealed record GattServerLayout(
    string ServiceUuid,
    string TokenServiceUuid,
    IReadOnlyList<GattServerCharacteristic> Characteristics);

/// <summary>
/// What centrals do with the published service. An implementation calls
/// these from whatever thread the OS uses, and holds a write's answer
/// until <see cref="WrittenAsync"/> completes: that wait is the
/// backpressure a central gets.
/// </summary>
public interface IGattCentrals
{
    /// <summary>The value from <paramref name="offset"/> on, or null when the read is to be refused.</summary>
    byte[]? Read(string uuid, int offset);

    /// <summary>False when the write is to be refused.</summary>
    Task<bool> WrittenAsync(string deviceId, string uuid, byte[] data, uint? mtu);

    /// <summary>Whether any central is subscribed to <paramref name="uuid"/> now.</summary>
    Task SubscriptionChangedAsync(string uuid, bool subscribed);

    /// <summary>The OS stopped advertising by itself.</summary>
    void Withdrawn();
}

/// <summary>A published service. Disposal withdraws it and its advertisement.</summary>
public interface IGattServer : IAsyncDisposable
{
    /// <summary>Notifies every subscribed central; the OS offers no narrower aim that all hosts share.</summary>
    Task NotifyAsync(string uuid, byte[] data, CancellationToken stop);
}

/// <summary>
/// The OS calls that publish a GATT service and advertise it. It throws
/// <see cref="HardwareFailureException"/> when the OS says why it cannot.
/// </summary>
public interface IGattServerHost
{
    Task<IGattServer> PublishAsync(GattServerLayout layout, IGattCentrals centrals, CancellationToken stop);
}

/// <summary>
/// The peripheral port over a GATT server, with the service, the events
/// and their order of <c>linux-hw</c>'s peripheral
/// (<c>src/bluez/peripheral.rs</c>).
/// </summary>
public sealed class GattPeripheral(IGattServerHost host) : IAdvertising
{
    private const string ExchangePayload = "a1b2c3d4-e5f6-7890-abcd-ef1234567891";

    /// <summary>
    /// The characteristics the phones' GATT servers expose (android
    /// <c>BleUuids.kt</c>), so a phone's central finds the same service.
    /// </summary>
    private static readonly GattServerCharacteristic[] Exposed =
    {
        new(ExchangePayload, GattAccess.Read | GattAccess.Notify),
        new("a1b2c3d4-e5f6-7890-abcd-ef1234567892", GattAccess.Write | GattAccess.Notify),
        new("a1b2c3d4-e5f6-7890-abcd-ef1234567893", GattAccess.Write | GattAccess.Notify),
        new("a1b2c3d4-e5f6-7890-abcd-ef1234567894", GattAccess.Write),
        new("a1b2c3d4-e5f6-7890-abcd-ef1234567895", GattAccess.Write | GattAccess.Notify),
        new("a1b2c3d4-e5f6-7890-abcd-ef1234567896", GattAccess.WriteWithoutResponse),
        new("a1b2c3d4-e5f6-7890-abcd-ef1234567897", GattAccess.Notify),
    };

    public async Task AdvertiseAsync(
        string serviceUuid,
        byte[] payload,
        ChannelReader<Notification> outbound,
        EventSink events,
        CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(outbound);
        ArgumentNullException.ThrowIfNull(events);
        if (!BleObservations.TryServiceUuid(serviceUuid, out Guid service))
        {
            throw new HardwareFailureException(HardwareFailure.Failed);
        }
        var layout = new GattServerLayout(service.ToString("D"), TokenServiceUuid(payload), Exposed);
        var centrals = new Centrals(payload, events, stop);
        IGattServer server = await host.PublishAsync(layout, centrals, stop).ConfigureAwait(false);
        await using (server.ConfigureAwait(false))
        {
            // Ends the wait for Core's next notification when the centrals' side ended the operation.
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(stop);
            try
            {
                await ServeAsync(server, outbound, centrals.Ended, waiting.Token).ConfigureAwait(false);
            }
            finally
            {
                waiting.Cancel();
            }
        }
    }

    /// <summary>
    /// Core's token as the phones advertise it: its first two bytes,
    /// big-endian, as a 16-bit Bluetooth-base service UUID, the only token
    /// channel iOS peripherals have (android
    /// <c>BleUuids.tokenToServiceUuid</c>).
    /// </summary>
    internal static string TokenServiceUuid(byte[] payload)
    {
        byte first = payload.Length > 0 ? payload[0] : (byte)0;
        byte second = payload.Length > 1 ? payload[1] : (byte)0;
        return $"0000{first:x2}{second:x2}-0000-1000-8000-00805f9b34fb";
    }

    private static async Task ServeAsync(
        IGattServer server,
        ChannelReader<Notification> outbound,
        Task ended,
        CancellationToken waiting)
    {
        while (true)
        {
            Task<bool> more = outbound.WaitToReadAsync(waiting).AsTask();
            if (await Task.WhenAny(ended, more).ConfigureAwait(false) == ended)
            {
                // Awaiting it is what raises the failure of a withdrawn advertisement.
                await ended.ConfigureAwait(false);
                return;
            }
            if (!await more.ConfigureAwait(false))
            {
                return;
            }
            while (outbound.TryRead(out Notification? notification))
            {
                if (Find(notification.Uuid) is { } characteristic && characteristic.Access.HasFlag(GattAccess.Notify))
                {
                    await server.NotifyAsync(characteristic.Uuid, notification.Data, waiting).ConfigureAwait(false);
                }
            }
        }
    }

    private static GattServerCharacteristic? Find(string uuid)
    {
        foreach (GattServerCharacteristic characteristic in Exposed)
        {
            if (string.Equals(characteristic.Uuid, uuid, StringComparison.OrdinalIgnoreCase))
            {
                return characteristic;
            }
        }
        return null;
    }

    private sealed class Centrals(byte[] payload, EventSink events, CancellationToken stop) : IGattCentrals
    {
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// The OS reports centrals on several threads at once; one reports
        /// at a time, so a central's chunks stay in the order it wrote them.
        /// Never disposed: a callback may still be inside it when the
        /// operation ends, and it holds no handle.
        /// </summary>
        private readonly SemaphoreSlim _reporting = new(1, 1);

        private readonly HashSet<string> _subscribed = new(StringComparer.Ordinal);
        private uint? _reportedMtu;

        /// <summary>Completes when Core takes no more of this operation; fails when the OS withdrew it.</summary>
        internal Task Ended => _ended.Task;

        public byte[]? Read(string uuid, int offset)
        {
            if (Find(uuid) is not { } characteristic || offset < 0)
            {
                return null;
            }
            byte[] value = characteristic.Uuid == ExchangePayload ? payload : Array.Empty<byte>();
            return offset <= value.Length ? value[offset..] : null;
        }

        public async Task<bool> WrittenAsync(string deviceId, string uuid, byte[] data, uint? mtu)
        {
            if (Find(uuid) is not { } characteristic
                || (characteristic.Access & (GattAccess.Write | GattAccess.WriteWithoutResponse)) == 0
                || data is not { Length: <= BleBounds.MaxValue })
            {
                return false;
            }
            // A central the contract cannot name is reported unaddressed, Core's wildcard for the link.
            string central = deviceId is { Length: <= BleBounds.MaxDeviceId } ? deviceId : "";
            return await Reporting(async () =>
            {
                if (mtu is { } negotiated && _reportedMtu != negotiated)
                {
                    _reportedMtu = negotiated;
                    if (!await Report(new Event.BleMtuNegotiated { DeviceId = central, Mtu = negotiated })
                        .ConfigureAwait(false))
                    {
                        return false;
                    }
                }
                return await Report(new Event.BleCharacteristicNotified
                {
                    DeviceId = central,
                    Direction = BleLinkDirection.Inbound,
                    Uuid = characteristic.Uuid,
                    Data = data,
                }).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        public async Task SubscriptionChangedAsync(string uuid, bool subscribed)
        {
            if (Find(uuid) is not { } characteristic || !characteristic.Access.HasFlag(GattAccess.Notify))
            {
                return;
            }
            await Reporting(async () =>
            {
                if (subscribed)
                {
                    bool first = _subscribed.Count == 0;
                    if (!_subscribed.Add(characteristic.Uuid) || !first)
                    {
                        return true;
                    }
                    // The OS names no central for a subscription; the writes that follow carry it.
                    return await Report(new Event.BleConnected { DeviceId = "", Direction = BleLinkDirection.Inbound })
                            .ConfigureAwait(false)
                        && await Report(new Event.BleGattReady { DeviceId = "" }).ConfigureAwait(false);
                }
                if (!_subscribed.Remove(characteristic.Uuid) || _subscribed.Count > 0)
                {
                    return true;
                }
                return await Report(new Event.BleDisconnected
                {
                    DeviceId = "",
                    Direction = BleLinkDirection.Inbound,
                    Reason = "central unsubscribed",
                }).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        public void Withdrawn() => _ended.TrySetException(new HardwareFailureException(HardwareFailure.Failed));

        private async Task<bool> Reporting(Func<Task<bool>> report)
        {
            try
            {
                await _reporting.WaitAsync(stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A callback the OS delivers after the operation ended.
                return false;
            }
            try
            {
                return await report().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            finally
            {
                _reporting.Release();
            }
        }

        private async Task<bool> Report(Event observation)
        {
            if (await events.SendAsync(observation, stop).ConfigureAwait(false) != SendOutcome.Ended)
            {
                return true;
            }
            _ended.TrySetResult();
            return false;
        }
    }
}
