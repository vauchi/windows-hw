// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

// The capability probe and a discovery measurement on a real machine
// (ADR-077 item 9). It prints the report Core would receive, then scans
// for a service UUID and prints each observation with the milliseconds
// since the scan command.
//
//   Probe [service uuid] [seconds]
//   Probe nfc [seconds]        wait for a card on any reader and say what
//                              the smart-card service answers
//   Probe survey [seconds]     which service UUIDs are advertised nearby,
//                              with no filter: what a scan could find

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vauchi.Contract;
using Vauchi.WindowsHw;
using Vauchi.WindowsHw.Native;
using Windows.Devices.Bluetooth.Advertisement;

if (args.Length > 0 && args[0] == "nfc")
{
    using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(args.Length > 1 ? int.Parse(args[1]) : 5));
    try
    {
        using INfcCard card = await new PcscReader(new WinSCard()).WaitForTagAsync(patience.Token);
        Console.WriteLine("a card is connected");
    }
    catch (NfcException refused)
    {
        Console.WriteLine("the smart-card service says: " + refused.Message);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("a reader is there and no card arrived");
    }
    return;
}

if (args.Length > 0 && args[0] == "survey")
{
    var advertisers = new Dictionary<Guid, HashSet<ulong>>();
    int heard = 0;
    var unfiltered = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
    unfiltered.Received += (_, advertisement) =>
    {
        lock (advertisers)
        {
            heard++;
            foreach (Guid uuid in advertisement.Advertisement.ServiceUuids)
            {
                if (!advertisers.TryGetValue(uuid, out HashSet<ulong>? from))
                {
                    advertisers[uuid] = from = new HashSet<ulong>();
                }
                from.Add(advertisement.BluetoothAddress);
            }
        }
    };
    unfiltered.Start();
    await Task.Delay(TimeSpan.FromSeconds(args.Length > 1 ? int.Parse(args[1]) : 5));
    unfiltered.Stop();
    lock (advertisers)
    {
        Console.WriteLine($"{heard} advertisement(s); service UUIDs and how many devices advertise each:");
        foreach ((Guid uuid, HashSet<ulong> from) in advertisers.OrderByDescending(entry => entry.Value.Count))
        {
            Console.WriteLine($"  {uuid}  {from.Count}");
        }
    }
    return;
}

if (args.Length > 0 && args[0] == "publish")
{
    // publish [seconds]: each OS step of the peripheral by itself, with what
    // Windows answers, for a machine where the port only says "failed".
    var steps = new List<Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProvider>();
    async Task Step(string uuid, bool withCharacteristic)
    {
        try
        {
            var created = await Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProvider.CreateAsync(Guid.Parse(uuid));
            Console.WriteLine($"create {uuid}: {created.Error}");
            if (created.ServiceProvider is null)
            {
                return;
            }
            steps.Add(created.ServiceProvider);
            if (withCharacteristic)
            {
                var parameters = new Windows.Devices.Bluetooth.GenericAttributeProfile.GattLocalCharacteristicParameters
                {
                    CharacteristicProperties =
                        Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties.Read
                        | Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties.Notify,
                };
                var characteristic = await created.ServiceProvider.Service.CreateCharacteristicAsync(
                    Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567891"), parameters);
                Console.WriteLine($"  characteristic: {characteristic.Error}");
            }
            created.ServiceProvider.AdvertisementStatusChanged += (_, changed) =>
                Console.WriteLine($"  status {uuid}: {changed.Status} error {changed.Error}");
            created.ServiceProvider.StartAdvertising(
                new Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProviderAdvertisingParameters
                {
                    IsConnectable = true, IsDiscoverable = true,
                });
            Console.WriteLine($"  advertising started: {created.ServiceProvider.AdvertisementStatus}");
        }
        catch (Exception refused)
        {
            Console.WriteLine($"  threw 0x{refused.HResult:X8} {refused.GetType().Name}: {refused.Message}");
        }
    }

    await Step("a1b2c3d4-e5f6-7890-abcd-ef1234567890", withCharacteristic: true);
    await Step("0000abcd-0000-1000-8000-00805f9b34fb", withCharacteristic: false);
    await Task.Delay(TimeSpan.FromSeconds(args.Length > 1 ? int.Parse(args[1]) : 3));
    foreach (var provider in steps)
    {
        Console.WriteLine($"final status: {provider.AdvertisementStatus}");
        provider.StopAdvertising();
    }
    return;
}

if (args.Length > 1 && args[0] == "steps")
{
    // steps <service-uuid> [seconds]: the central's OS calls one at a time
    // against the first peer advertising the service, with what Windows
    // answers to each, for a link the port only reports as failed.
    var heard = new TaskCompletionSource<BluetoothLEAdvertisementReceivedEventArgs>();
    var listening = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
    listening.AdvertisementFilter.Advertisement.ServiceUuids.Add(Guid.Parse(args[1]));
    listening.Received += (_, advertisement) => heard.TrySetResult(advertisement);
    listening.Start();
    var first = await heard.Task.WaitAsync(TimeSpan.FromSeconds(args.Length > 2 ? int.Parse(args[2]) : 60));
    listening.Stop();
    Console.WriteLine($"heard {first.BluetoothAddress} type {first.BluetoothAddressType} rssi {first.RawSignalStrengthInDBm} connectable {first.IsConnectable}");
    async Task<T?> Try<T>(string step, Func<Task<T>> call) where T : class
    {
        var clock = Stopwatch.StartNew();
        try
        {
            T result = await call();
            Console.WriteLine($"{clock.ElapsedMilliseconds,6} ms  {step}: {(result is null ? "null" : "ok")}");
            return result;
        }
        catch (Exception failed)
        {
            Console.WriteLine($"{clock.ElapsedMilliseconds,6} ms  {step}: threw 0x{failed.HResult:X8} {failed.GetType().Name}: {failed.Message.Trim()}");
            return null;
        }
    }
    var peer = await Try("FromBluetoothAddressAsync(address)",
        () => Windows.Devices.Bluetooth.BluetoothLEDevice.FromBluetoothAddressAsync(first.BluetoothAddress).AsTask());
    peer ??= await Try("FromBluetoothAddressAsync(address, type)",
        () => Windows.Devices.Bluetooth.BluetoothLEDevice.FromBluetoothAddressAsync(first.BluetoothAddress, first.BluetoothAddressType).AsTask());
    if (peer is null)
    {
        return;
    }
    Console.WriteLine($"device id {peer.BluetoothDeviceId.Id} status {peer.ConnectionStatus}");
    var session = await Try("GattSession.FromDeviceIdAsync",
        () => Windows.Devices.Bluetooth.GenericAttributeProfile.GattSession.FromDeviceIdAsync(peer.BluetoothDeviceId).AsTask());
    if (session is not null)
    {
        session.MaintainConnection = true;
        Console.WriteLine($"session max pdu {session.MaxPduSize} status {session.SessionStatus}");
    }
    foreach (var mode in new[] { Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached, Windows.Devices.Bluetooth.BluetoothCacheMode.Cached })
    {
        var services = await Try($"GetGattServicesAsync({mode})", () => peer.GetGattServicesAsync(mode).AsTask());
        if (services is null)
        {
            continue;
        }
        Console.WriteLine($"  status {services.Status} protocol error {services.ProtocolError} services {services.Services.Count}; peer status {peer.ConnectionStatus}");
        foreach (var gattService in services.Services)
        {
            var characteristics = await Try($"  {gattService.Uuid} characteristics", () => gattService.GetCharacteristicsAsync(Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached).AsTask());
            if (characteristics is null)
            {
                continue;
            }
            Console.WriteLine($"    status {characteristics.Status} count {characteristics.Characteristics.Count}");
            foreach (var characteristic in characteristics.Characteristics)
            {
                Console.WriteLine($"    {characteristic.Uuid} {characteristic.CharacteristicProperties}");
                if ((characteristic.CharacteristicProperties & (Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties.Notify | Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties.Indicate)) == 0)
                {
                    continue;
                }
                var how = characteristic.CharacteristicProperties.HasFlag(Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties.Notify)
                    ? Windows.Devices.Bluetooth.GenericAttributeProfile.GattClientCharacteristicConfigurationDescriptorValue.Notify
                    : Windows.Devices.Bluetooth.GenericAttributeProfile.GattClientCharacteristicConfigurationDescriptorValue.Indicate;
                var subscribed = await Try<object>("      subscribe", async () =>
                    (object)await characteristic.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(how));
                if (subscribed is Windows.Devices.Bluetooth.GenericAttributeProfile.GattWriteResult written)
                {
                    Console.WriteLine($"      -> {written.Status} protocol error {written.ProtocolError}");
                }
            }
        }
        break;
    }
    Console.WriteLine($"max pdu after discovery {session?.MaxPduSize}; peer status {peer.ConnectionStatus}");
    session?.Dispose();
    peer.Dispose();
    return;
}

if (args.Length > 1 && args[0] == "dial")
{
    // dial <service-uuid> [seconds]: the first device advertising the
    // service is dialled, and what the link reports is printed.
    int patience = args.Length > 2 ? int.Parse(args[2]) : 15;
    var radio = new Backends { Discovery = new WinRtDiscovery(), Central = new GattCentral(new WinRtGattDialer()) };
    await using var dialling = new HardwareShell(radio, await WindowsProbe.ProbeAsync());
    Console.WriteLine($"scan {args[1]}: {dialling.Execute(new Command.BleStartScanning { ServiceUuid = args[1] })}");
    using var until = new CancellationTokenSource(TimeSpan.FromSeconds(patience));
    var since = Stopwatch.StartNew();
    bool dialled = false;
    while (await dialling.NextAsync(until.Token) is { } reported)
    {
        if (reported is Event.BleAdvertisementObserved seen)
        {
            if (!dialled)
            {
                dialled = true;
                Console.WriteLine($"stop scan: {dialling.Execute(new Command.BleStopScanning())}");
                Console.WriteLine($"dial {seen.Value.Id}: {dialling.Execute(new Command.BleConnect { DeviceId = seen.Value.Id })}");
            }
            continue;
        }
        Console.WriteLine($"{since.ElapsedMilliseconds,6} ms  " + Encoding.UTF8.GetString(ContractJson.EncodeEvent(reported)));
    }
    Console.WriteLine(dialled ? "done" : $"nothing advertised {args[1]} in {patience} s");
    return;
}

string service = args.Length > 0 ? args[0] : "0000fd6f-0000-1000-8000-00805f9b34fb";
int seconds = args.Length > 1 ? int.Parse(args[1]) : 5;

DeviceCapabilities device = await WindowsProbe.ProbeAsync();
await using var shell = new HardwareShell(new Backends { Discovery = new WinRtDiscovery() }, device);
Console.WriteLine("device:   " + Encoding.UTF8.GetString(ContractJson.EncodeEvent(
    new Event.HardwareCapabilitiesReported { Capabilities = device })));
Console.WriteLine("reported: " + Encoding.UTF8.GetString(ContractJson.EncodeEvent(shell.Report())));

var clock = Stopwatch.StartNew();
Console.WriteLine($"scan {service}: {shell.Execute(new Command.BleStartScanning { ServiceUuid = service })}");
using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
int observed = 0;
while (await shell.NextAsync(limit.Token) is { } observation)
{
    observed++;
    Console.WriteLine($"{clock.ElapsedMilliseconds,6} ms  " + Encoding.UTF8.GetString(ContractJson.EncodeEvent(observation)));
}
Console.WriteLine($"stop: {shell.Execute(new Command.BleStopScanning())}; {observed} observation(s) in {seconds} s");
