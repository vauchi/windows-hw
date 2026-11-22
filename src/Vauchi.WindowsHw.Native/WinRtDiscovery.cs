// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Vauchi.Contract;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace Vauchi.WindowsHw.Native;

/// <summary>
/// The discovery port on WinRT: an advertisement watcher filtered to
/// Core's service UUID. It reports what it sees and decides nothing.
/// </summary>
public sealed class WinRtDiscovery : IDiscovery
{
    /// <summary>
    /// Advertisements waiting between the watcher's callback thread and
    /// the inbox. The callback must not block, so a newer one is dropped
    /// while these wait; an advertiser repeats itself several times a
    /// second.
    /// </summary>
    private const int Waiting = 8;

    public async Task ScanAsync(string serviceUuid, EventSink events, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (!BleObservations.TryServiceUuid(serviceUuid, out Guid service))
        {
            throw new HardwareFailureException(HardwareFailure.Failed);
        }
        Channel<Event> seen = Channel.CreateBounded<Event>(new BoundedChannelOptions(Waiting)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(service);
        watcher.Received += (_, advertisement) => seen.Writer.TryWrite(BleObservations.Observed(
            advertisement.BluetoothAddress,
            advertisement.RawSignalStrengthInDBm,
            advertisement.Advertisement.ServiceUuids.ToArray()));
        watcher.Stopped += (_, stopped) => seen.Writer.TryComplete(
            stopped.Error == BluetoothError.Success ? null : new HardwareFailureException(Failure(stopped.Error)));
        try
        {
            watcher.Start();
        }
        catch (Exception refused)
        {
            // With the radio off Start throws instead of raising Stopped.
            throw new HardwareFailureException(OsFailures.From(refused.HResult));
        }
        try
        {
            await foreach (Event observed in seen.Reader.ReadAllAsync(stop).ConfigureAwait(false))
            {
                if (await events.SendAsync(observed, stop).ConfigureAwait(false) == SendOutcome.Ended)
                {
                    return;
                }
            }
        }
        finally
        {
            watcher.Stop();
        }
    }

    internal static HardwareFailure Failure(BluetoothError error) => error switch
    {
        BluetoothError.RadioNotAvailable or BluetoothError.DeviceNotConnected or BluetoothError.NotSupported
            => HardwareFailure.Unavailable,
        BluetoothError.DisabledByPolicy or BluetoothError.DisabledByUser or BluetoothError.ConsentRequired
            => HardwareFailure.PermissionDenied,
        _ => HardwareFailure.Failed,
    };
}
