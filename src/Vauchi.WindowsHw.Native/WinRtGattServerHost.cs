// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Security.Cryptography;

namespace Vauchi.WindowsHw.Native;

/// <summary>
/// Publishes the exchange's GATT service through WinRT and advertises it.
/// What a central's reads, writes and subscriptions mean is
/// <see cref="GattPeripheral"/>'s; this only makes the OS calls.
/// </summary>
public sealed class WinRtGattServerHost : IGattServerHost
{
    public async Task<IGattServer> PublishAsync(GattServerLayout layout, IGattCentrals centrals, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(centrals);
        var server = new Server(centrals);
        try
        {
            await server.PublishAsync(layout, stop).ConfigureAwait(false);
            return server;
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Server(IGattCentrals centrals) : IGattServer
    {
        private readonly Dictionary<string, GattLocalCharacteristic> _published = new(StringComparer.Ordinal);
        private readonly List<GattServiceProvider> _providers = new();
        private readonly Dictionary<GattServiceProvider, TaskCompletionSource> _started = new();

        internal async Task PublishAsync(GattServerLayout layout, CancellationToken stop)
        {
            GattServiceProvider service = await ProviderAsync(layout.ServiceUuid, stop).ConfigureAwait(false);
            foreach (GattServerCharacteristic characteristic in layout.Characteristics)
            {
                var parameters = new GattLocalCharacteristicParameters
                {
                    CharacteristicProperties = Properties(characteristic.Access),
                    ReadProtectionLevel = GattProtectionLevel.Plain,
                    WriteProtectionLevel = GattProtectionLevel.Plain,
                };
                GattLocalCharacteristicResult created = await Os(
                    () => service.Service.CreateCharacteristicAsync(Guid.Parse(characteristic.Uuid), parameters),
                    stop).ConfigureAwait(false);
                if (created.Error != BluetoothError.Success)
                {
                    throw new HardwareFailureException(WinRtDiscovery.Failure(created.Error));
                }
                GattLocalCharacteristic local = created.Characteristic;
                local.ReadRequested += OnRead;
                local.WriteRequested += OnWrite;
                local.SubscribedClientsChanged += OnSubscribers;
                _published[characteristic.Uuid] = local;
            }
            // A provider advertises its own service UUID and no other, so the
            // token gets a provider of its own with an empty service.
            GattServiceProvider token = await ProviderAsync(layout.TokenServiceUuid, stop).ConfigureAwait(false);
            var advertising = new GattServiceProviderAdvertisingParameters { IsConnectable = true, IsDiscoverable = true };
            try
            {
                service.StartAdvertising(advertising);
                token.StartAdvertising(advertising);
            }
            catch (Exception refused)
            {
                // With the radio off StartAdvertising throws instead of reporting a status.
                throw new HardwareFailureException(OsFailures.From(refused.HResult));
            }
            await Task.WhenAll(_started[service].Task, _started[token].Task).WaitAsync(stop).ConfigureAwait(false);
        }

        public async Task NotifyAsync(string uuid, byte[] data, CancellationToken stop)
        {
            if (!_published.TryGetValue(uuid, out GattLocalCharacteristic? local))
            {
                return;
            }
            try
            {
                await local.NotifyValueAsync(CryptographicBuffer.CreateFromByteArray(data)).AsTask(stop)
                    .ConfigureAwait(false);
            }
            catch (Exception failed) when (failed is not OperationCanceledException)
            {
                // A central that left between Core's write and here; its loss is reported by the subscription.
            }
        }

        public ValueTask DisposeAsync()
        {
            foreach (GattLocalCharacteristic local in _published.Values)
            {
                local.ReadRequested -= OnRead;
                local.WriteRequested -= OnWrite;
                local.SubscribedClientsChanged -= OnSubscribers;
            }
            _published.Clear();
            foreach (GattServiceProvider provider in _providers)
            {
                provider.AdvertisementStatusChanged -= OnAdvertisementStatus;
                try
                {
                    provider.StopAdvertising();
                }
                catch (Exception)
                {
                    // A provider that never started, or a radio that went away: there is nothing left to stop.
                }
            }
            _providers.Clear();
            _started.Clear();
            return ValueTask.CompletedTask;
        }

        private async Task<GattServiceProvider> ProviderAsync(string uuid, CancellationToken stop)
        {
            GattServiceProviderResult created =
                await Os(() => GattServiceProvider.CreateAsync(Guid.Parse(uuid)), stop).ConfigureAwait(false);
            if (created.Error != BluetoothError.Success)
            {
                throw new HardwareFailureException(WinRtDiscovery.Failure(created.Error));
            }
            _started[created.ServiceProvider] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            created.ServiceProvider.AdvertisementStatusChanged += OnAdvertisementStatus;
            _providers.Add(created.ServiceProvider);
            return created.ServiceProvider;
        }

        private void OnAdvertisementStatus(
            GattServiceProvider sender,
            GattServiceProviderAdvertisementStatusChangedEventArgs arguments)
        {
            if (!_started.TryGetValue(sender, out TaskCompletionSource? started))
            {
                return;
            }
            if (arguments.Status == GattServiceProviderAdvertisementStatus.Started)
            {
                started.TrySetResult();
                return;
            }
            if (arguments.Status != GattServiceProviderAdvertisementStatus.Aborted)
            {
                return;
            }
            // Observed on Windows 11 (AC, 2026-11-22): a provider reports Aborted
            // with no error right after StartAdvertising, then Started. Only an
            // error, or Aborted after Started, is the OS giving up.
            if (started.Task.IsCompletedSuccessfully)
            {
                centrals.Withdrawn();
            }
            else if (arguments.Error != BluetoothError.Success)
            {
                started.TrySetException(new HardwareFailureException(WinRtDiscovery.Failure(arguments.Error)));
            }
        }

        private async void OnRead(GattLocalCharacteristic sender, GattReadRequestedEventArgs arguments)
        {
            Deferral deferral = arguments.GetDeferral();
            try
            {
                GattReadRequest? request = await arguments.GetRequestAsync();
                if (request is null)
                {
                    return;
                }
                byte[]? value = request.Offset <= GattCentral.MaxAttributeValue
                    ? centrals.Read(sender.Uuid.ToString("D"), (int)request.Offset)
                    : null;
                if (value is null)
                {
                    request.RespondWithProtocolError(GattProtocolError.InvalidOffset);
                }
                else
                {
                    request.RespondWithValue(CryptographicBuffer.CreateFromByteArray(value));
                }
            }
            catch (Exception)
            {
                // An exception in an OS callback has no caller and would end the process; the central sees a timeout.
            }
            finally
            {
                deferral.Complete();
            }
        }

        private async void OnWrite(GattLocalCharacteristic sender, GattWriteRequestedEventArgs arguments)
        {
            Deferral deferral = arguments.GetDeferral();
            try
            {
                GattWriteRequest? request = await arguments.GetRequestAsync();
                if (request is null)
                {
                    return;
                }
                bool answers = request.Option == GattWriteOption.WriteWithResponse;
                // Bounded before it is copied; a write in parts is not what the phones send.
                byte? refusal =
                    request.Offset != 0 ? GattProtocolError.InvalidOffset
                    : request.Value.Length > GattCentral.MaxAttributeValue ? GattProtocolError.InvalidAttributeValueLength
                    : null;
                if (refusal is null)
                {
                    CryptographicBuffer.CopyToByteArray(request.Value, out byte[]? data);
                    bool taken = await centrals.WrittenAsync(
                        BleObservations.CentralId(arguments.Session.DeviceId.Id),
                        sender.Uuid.ToString("D"),
                        data ?? Array.Empty<byte>(),
                        arguments.Session.MaxPduSize).ConfigureAwait(false);
                    refusal = taken ? null : GattProtocolError.WriteNotPermitted;
                }
                if (!answers)
                {
                    return;
                }
                if (refusal is byte error)
                {
                    request.RespondWithProtocolError(error);
                }
                else
                {
                    request.Respond();
                }
            }
            catch (Exception)
            {
                // As for a read: nothing may escape an OS callback.
            }
            finally
            {
                deferral.Complete();
            }
        }

        private async void OnSubscribers(GattLocalCharacteristic sender, object arguments)
        {
            try
            {
                await centrals.SubscriptionChangedAsync(sender.Uuid.ToString("D"), sender.SubscribedClients.Count > 0)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // As for a read: nothing may escape an OS callback.
            }
        }

        private static GattCharacteristicProperties Properties(GattAccess access)
        {
            GattCharacteristicProperties properties = GattCharacteristicProperties.None;
            if (access.HasFlag(GattAccess.Read))
            {
                properties |= GattCharacteristicProperties.Read;
            }
            if (access.HasFlag(GattAccess.Write))
            {
                properties |= GattCharacteristicProperties.Write;
            }
            if (access.HasFlag(GattAccess.WriteWithoutResponse))
            {
                properties |= GattCharacteristicProperties.WriteWithoutResponse;
            }
            if (access.HasFlag(GattAccess.Notify))
            {
                properties |= GattCharacteristicProperties.Notify;
            }
            return properties;
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
    }
}
