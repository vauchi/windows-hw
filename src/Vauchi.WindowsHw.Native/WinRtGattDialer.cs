// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace Vauchi.WindowsHw.Native;

/// <summary>
/// Reaches a peer's GATT server through WinRT. What the link reports and
/// in which order is <see cref="GattCentral"/>'s; this only makes the OS
/// calls.
/// </summary>
public sealed class WinRtGattDialer : IGattDialer
{
    public async Task<IGattLink> DialAsync(ulong address, IGattSignals signals, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(signals);
        BluetoothLEDevice? device = await Os(() => BluetoothLEDevice.FromBluetoothAddressAsync(address), stop)
            .ConfigureAwait(false);
        if (device is null)
        {
            // Windows knows no device at this address: it was never seen, or the radio is off.
            throw new HardwareFailureException(HardwareFailure.Failed);
        }
        var link = new Link(device, signals);
        try
        {
            await link.ResolveAsync(stop).ConfigureAwait(false);
            return link;
        }
        catch
        {
            await link.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<T> Os<T>(Func<IAsyncOperation<T>> call, CancellationToken stop)
    {
        try
        {
            return await call().AsTask(stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception refused)
        {
            throw new HardwareFailureException(OsFailures.From(refused.HResult));
        }
    }

    private sealed class Link(BluetoothLEDevice device, IGattSignals signals) : IGattLink
    {
        private readonly Dictionary<string, Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristic> _found =
            new(StringComparer.Ordinal);

        private readonly List<GattDeviceService> _services = new();
        private GattSession? _session;

        public uint? Mtu { get; private set; }

        public IReadOnlyList<GattCharacteristic> Characteristics { get; private set; } =
            Array.Empty<GattCharacteristic>();

        internal async Task ResolveAsync(CancellationToken stop)
        {
            device.ConnectionStatusChanged += OnConnectionStatus;
            _session = await Os(() => GattSession.FromDeviceIdAsync(device.BluetoothDeviceId), stop)
                .ConfigureAwait(false);
            // Without it Windows drops the link once no operation is pending.
            _session.MaintainConnection = true;
            _session.MaxPduSizeChanged += OnMaxPduSize;
            GattDeviceServicesResult services =
                await Os(() => device.GetGattServicesAsync(BluetoothCacheMode.Uncached), stop).ConfigureAwait(false);
            if (services.Status != GattCommunicationStatus.Success)
            {
                throw new HardwareFailureException(Failure(services.Status));
            }
            foreach (GattDeviceService service in services.Services)
            {
                if (GattCentral.IsStackService(service.Uuid))
                {
                    service.Dispose();
                    continue;
                }
                _services.Add(service);
            }
            var listed = new List<GattCharacteristic>();
            foreach (GattDeviceService service in _services)
            {
                GattCharacteristicsResult characteristics =
                    await Os(() => service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached), stop)
                        .ConfigureAwait(false);
                if (characteristics.Status == GattCommunicationStatus.AccessDenied)
                {
                    // Windows keeps some services to itself; Core names none of them.
                    continue;
                }
                if (characteristics.Status != GattCommunicationStatus.Success)
                {
                    throw new HardwareFailureException(HardwareFailure.Failed);
                }
                foreach (var characteristic in characteristics.Characteristics)
                {
                    string uuid = characteristic.Uuid.ToString("D");
                    if (_found.TryAdd(uuid, characteristic))
                    {
                        listed.Add(new GattCharacteristic(uuid, Notifies(characteristic)));
                    }
                }
            }
            Characteristics = listed;
            // After discovery: until Windows has connected, the session names ATT's default.
            Mtu = _session.MaxPduSize;
        }

        public async Task<bool> SubscribeAsync(string uuid, CancellationToken stop)
        {
            if (!_found.TryGetValue(uuid, out var characteristic))
            {
                return false;
            }
            GattClientCharacteristicConfigurationDescriptorValue how =
                characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
                    ? GattClientCharacteristicConfigurationDescriptorValue.Notify
                    : GattClientCharacteristicConfigurationDescriptorValue.Indicate;
            characteristic.ValueChanged += OnValueChanged;
            return await Did(
                () => characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(how),
                stop).ConfigureAwait(false) == GattCommunicationStatus.Success;
        }

        public async Task<bool> WriteAsync(string uuid, byte[] data, CancellationToken stop) =>
            _found.TryGetValue(uuid, out var characteristic)
            && await Did(
                () => characteristic.WriteValueAsync(
                    CryptographicBuffer.CreateFromByteArray(data),
                    GattWriteOption.WriteWithResponse),
                stop).ConfigureAwait(false) == GattCommunicationStatus.Success;

        public async Task<byte[]?> ReadAsync(string uuid, CancellationToken stop)
        {
            if (!_found.TryGetValue(uuid, out var characteristic))
            {
                return null;
            }
            try
            {
                GattReadResult read = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached)
                    .AsTask(stop).ConfigureAwait(false);
                return read.Status == GattCommunicationStatus.Success && read.Value.Length <= GattCentral.MaxAttributeValue
                    ? Bytes(read.Value)
                    : null;
            }
            catch (Exception failed) when (failed is not OperationCanceledException)
            {
                return null;
            }
        }

        public ValueTask DisposeAsync()
        {
            device.ConnectionStatusChanged -= OnConnectionStatus;
            foreach (var characteristic in _found.Values)
            {
                characteristic.ValueChanged -= OnValueChanged;
            }
            _found.Clear();
            if (_session is not null)
            {
                _session.MaxPduSizeChanged -= OnMaxPduSize;
                _session.Dispose();
            }
            foreach (GattDeviceService service in _services)
            {
                service.Dispose();
            }
            _services.Clear();
            // Windows disconnects once nothing holds the device.
            device.Dispose();
            return ValueTask.CompletedTask;
        }

        private void OnConnectionStatus(BluetoothLEDevice sender, object arguments)
        {
            Guarded(() =>
            {
                if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
                {
                    signals.Lost();
                }
            });
        }

        private void OnMaxPduSize(GattSession sender, object arguments) =>
            Guarded(() => signals.MtuChanged(sender.MaxPduSize));

        private void OnValueChanged(
            Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristic sender,
            GattValueChangedEventArgs arguments)
        {
            Guarded(() =>
            {
                // Bounded before it is copied: an attribute value is at most 512 bytes.
                if (arguments.CharacteristicValue.Length <= GattCentral.MaxAttributeValue)
                {
                    signals.Notified(sender.Uuid.ToString("D"), Bytes(arguments.CharacteristicValue));
                }
            });
        }

        /// <summary>
        /// A callback Windows delivers while the link is being released
        /// reads a closed object. Its exception has no caller to reach and
        /// would end the process; the link is over either way.
        /// </summary>
        private static void Guarded(Action callback)
        {
            try
            {
                callback();
            }
            catch (Exception released) when (released is ObjectDisposedException or InvalidOperationException
                or System.Runtime.InteropServices.COMException)
            {
            }
        }

        private static async Task<GattCommunicationStatus> Did(
            Func<IAsyncOperation<GattCommunicationStatus>> call,
            CancellationToken stop)
        {
            try
            {
                return await call().AsTask(stop).ConfigureAwait(false);
            }
            catch (Exception failed) when (failed is not OperationCanceledException)
            {
                return GattCommunicationStatus.Unreachable;
            }
        }

        private static bool Notifies(Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristic characteristic) =>
            (characteristic.CharacteristicProperties
                & (GattCharacteristicProperties.Notify | GattCharacteristicProperties.Indicate)) != 0;

        private static byte[] Bytes(IBuffer buffer)
        {
            if (buffer.Length == 0)
            {
                return Array.Empty<byte>();
            }
            CryptographicBuffer.CopyToByteArray(buffer, out byte[] bytes);
            return bytes;
        }

        private static HardwareFailure Failure(GattCommunicationStatus status) => status switch
        {
            GattCommunicationStatus.AccessDenied => HardwareFailure.PermissionDenied,
            _ => HardwareFailure.Failed,
        };
    }
}
