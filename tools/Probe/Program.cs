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
