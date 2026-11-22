// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vauchi.Contract;
using Xunit;

namespace Vauchi.WindowsHw.Tests;

public class BluetoothTests
{
    private const string Service = "0000fd6f-0000-1000-8000-00805f9b34fb";

    private static Event Seen(string id) => new Event.BleAdvertisementObserved(
        new BleAdvertisementObservation { Id = id, Rssi = -40, ServiceUuids = Array.Empty<BleServiceUuid>() });

    private static Command Write(string peer, BleLinkDirection direction, byte[] data) =>
        new Command.BleWriteCharacteristic { DeviceId = peer, Direction = direction, Uuid = "c1", Data = data };

    [Fact]
    public async Task AScanReachesTheDiscoveryPortAndItsObservationsReachTheHost()
    {
        var discovery = new FakeDiscovery();
        await using var shell = new HardwareShell(new Backends { Discovery = discovery }, new DeviceCapabilities());

        Assert.Equal(ExecuteResult.Accepted, shell.Execute(new Command.BleStartScanning { ServiceUuid = Service }));
        Call<string> scan = await discovery.NextCall();
        Assert.Equal(Service, scan.Arguments);
        Assert.Equal(SendOutcome.Accepted, await scan.Send(Seen("one")));
        Assert.Equal(SendOutcome.Accepted, await scan.Send(Seen("two")));

        Assert.Equal(Seen("one").Wire(), (await shell.Next()).Wire());
        Assert.Equal(Seen("two").Wire(), (await shell.Next()).Wire());
        Assert.False(shell.TryTake(out _));
    }

    [Fact]
    public async Task StoppingAScanCancelsThePortDropsWhatItQueuedAndRefusesItsLateCallbacks()
    {
        var discovery = new FakeDiscovery();
        await using var shell = new HardwareShell(new Backends { Discovery = discovery }, new DeviceCapabilities());
        shell.Execute(new Command.BleStartScanning { ServiceUuid = Service });
        Call<string> scan = await discovery.NextCall();
        await scan.Send(Seen("stale"));

        Assert.Equal(ExecuteResult.Accepted, shell.Execute(new Command.BleStopScanning()));

        await scan.Stopped;
        Assert.False(shell.TryTake(out _));
        Assert.Equal(SendOutcome.Ended, await scan.Events.SendAsync(Seen("late"), default));
        Assert.False(shell.TryTake(out _));
    }

    [Fact]
    public async Task ASecondScanReplacesTheFirstAndOnlyItsObservationsArrive()
    {
        var discovery = new FakeDiscovery();
        await using var shell = new HardwareShell(new Backends { Discovery = discovery }, new DeviceCapabilities());
        shell.Execute(new Command.BleStartScanning { ServiceUuid = Service });
        Call<string> first = await discovery.NextCall();
        await first.Send(Seen("first"));

        shell.Execute(new Command.BleStartScanning { ServiceUuid = "other" });
        Call<string> second = await discovery.NextCall();
        await first.Stopped;
        await second.Send(Seen("second"));

        Assert.Equal("other", second.Arguments);
        Assert.Equal(Seen("second").Wire(), (await shell.Next()).Wire());
        Assert.False(shell.TryTake(out _));
    }

    public static TheoryData<Exception, string> Failures => new()
    {
        { new HardwareFailureException(HardwareFailure.Unavailable), "{\"HardwareUnavailable\":{\"transport\":\"BLE\"}}" },
        { new HardwareFailureException(HardwareFailure.PermissionDenied), "{\"PermissionDenied\":{\"transport\":\"BLE\"}}" },
        {
            new HardwareFailureException(HardwareFailure.Failed),
            "{\"HardwareError\":{\"error\":\"Bluetooth discovery failed\",\"transport\":\"BLE\"}}"
        },
        {
            new InvalidOperationException("0x800710DF at 12:34:56:78:9A:BC"),
            "{\"HardwareError\":{\"error\":\"Bluetooth discovery failed\",\"transport\":\"BLE\"}}"
        },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task APortFailureIsReportedAsTheContractsEventWithoutTheOperatingSystemsText(
        Exception failure,
        string expected)
    {
        var discovery = new FakeDiscovery();
        await using var shell = new HardwareShell(new Backends { Discovery = discovery }, new DeviceCapabilities());
        shell.Execute(new Command.BleStartScanning { ServiceUuid = Service });
        Call<string> scan = await discovery.NextCall();
        await scan.Send(Seen("before"));

        scan.Fail(failure);

        Assert.Equal(Seen("before").Wire(), (await shell.Next()).Wire());
        Assert.Equal(expected, (await shell.Next()).Wire());
    }

    [Fact]
    public async Task AFullInboxHoldsThePortBackUntilTheHostTakesAndNoObservationIsLost()
    {
        const int Sent = 200;
        var discovery = new FakeDiscovery();
        await using var shell = new HardwareShell(new Backends { Discovery = discovery }, new DeviceCapabilities());
        shell.Execute(new Command.BleStartScanning { ServiceUuid = Service });
        Call<string> scan = await discovery.NextCall();

        Task producer = Task.Run(async () =>
        {
            for (int index = 0; index < Sent; index++)
            {
                Assert.Equal(SendOutcome.Accepted, await scan.Send(Seen($"peer-{index}")));
            }
        });
        var received = new List<string>();
        while (received.Count < Sent)
        {
            received.Add((await shell.Next()).Wire());
        }
        await producer.WaitAsync(Wait.Limit);

        Assert.Equal(Enumerable.Range(0, Sent).Select(index => Seen($"peer-{index}").Wire()), received);
    }

    [Fact]
    public async Task ASendIntoAFullInboxWaitsForTheHostToTakeOneAndThenGoesThrough()
    {
        const int Budget = 64;
        var discovery = new FakeDiscovery();
        await using var shell = new HardwareShell(new Backends { Discovery = discovery }, new DeviceCapabilities());
        shell.Execute(new Command.BleStartScanning { ServiceUuid = Service });
        Call<string> scan = await discovery.NextCall();
        for (int index = 0; index < Budget; index++)
        {
            Assert.Equal(SendOutcome.Accepted, await scan.Send(Seen($"peer-{index}")));
        }

        Task<SendOutcome> held = scan.Send(Seen("held")).AsTask();
        // Completed here would mean the send gave up or dropped the event.
        Assert.False(held.IsCompleted);
        Assert.True(shell.TryTake(out Event? first));

        Assert.Equal(SendOutcome.Accepted, await held.WaitAsync(Wait.Limit));
        Assert.Equal(Seen("peer-0").Wire(), first!.Wire());
        for (int index = 1; index < Budget; index++)
        {
            Assert.Equal(Seen($"peer-{index}").Wire(), (await shell.Next()).Wire());
        }
        Assert.Equal(Seen("held").Wire(), (await shell.Next()).Wire());
    }

    [Fact]
    public async Task ASendHeldByAFullInboxEndsWhenItsOperationIsStopped()
    {
        var discovery = new FakeDiscovery();
        await using var shell = new HardwareShell(new Backends { Discovery = discovery }, new DeviceCapabilities());
        shell.Execute(new Command.BleStartScanning { ServiceUuid = Service });
        Call<string> scan = await discovery.NextCall();
        for (int index = 0; index < 64; index++)
        {
            await scan.Send(Seen($"peer-{index}"));
        }
        Task<SendOutcome> held = scan.Send(Seen("held")).AsTask();

        shell.Execute(new Command.BleStopScanning());

        Assert.Equal(SendOutcome.Ended, await held.WaitAsync(Wait.Limit));
        Assert.False(shell.TryTake(out _));
    }

    [Fact]
    public async Task AnObservationOverTheByteBudgetIsDroppedAndThePortIsTold()
    {
        var discovery = new FakeDiscovery();
        await using var shell = new HardwareShell(new Backends { Discovery = discovery }, new DeviceCapabilities());
        shell.Execute(new Command.BleStartScanning { ServiceUuid = Service });
        Call<string> scan = await discovery.NextCall();
        Event oversized = new Event.BleDeviceDiscovered { Id = "peer", Rssi = -40, AdvData = new byte[4096] };

        Assert.Equal(SendOutcome.Unfit, await scan.Send(oversized));
        Assert.Equal(SendOutcome.Accepted, await scan.Send(Seen("next")));

        Assert.Equal(Seen("next").Wire(), (await shell.Next()).Wire());
    }

    [Fact]
    public async Task AdvertisingReachesThePeripheralPortAndInboundWritesBecomeItsNotifications()
    {
        var advertising = new FakeAdvertising();
        await using var shell = new HardwareShell(new Backends { Advertising = advertising }, new DeviceCapabilities());
        var payload = new byte[] { 9, 8, 7 };

        Assert.Equal(
            ExecuteResult.Accepted,
            shell.Execute(new Command.BleStartAdvertising { ServiceUuid = Service, Payload = payload }));
        Call<Advertisement> advertisement = await advertising.NextCall();
        Assert.Equal(ExecuteResult.Accepted, shell.Execute(Write("central-1", BleLinkDirection.Inbound, new byte[] { 1 })));
        Assert.Equal(ExecuteResult.Accepted, shell.Execute(Write("central-1", BleLinkDirection.Inbound, new byte[] { 2 })));

        Assert.Equal(Service, advertisement.Arguments.ServiceUuid);
        Assert.Equal(payload, advertisement.Arguments.Payload);
        Notification first = await advertisement.Arguments.Outbound.ReadAsync().AsTask().WaitAsync(Wait.Limit);
        Notification second = await advertisement.Arguments.Outbound.ReadAsync().AsTask().WaitAsync(Wait.Limit);
        Assert.Equal(("central-1", "c1"), (first.DeviceId, first.Uuid));
        Assert.Equal(new byte[] { 1 }, first.Data);
        Assert.Equal(new byte[] { 2 }, second.Data);
    }

    [Fact]
    public async Task AnInboundWriteWithNoAdvertisementIsInactiveAndAFullQueueIsBackpressure()
    {
        var advertising = new FakeAdvertising();
        await using var shell = new HardwareShell(new Backends { Advertising = advertising }, new DeviceCapabilities());
        Command write = Write("central-1", BleLinkDirection.Inbound, new byte[] { 1 });

        Assert.Equal(ExecuteResult.Inactive, shell.Execute(write));

        shell.Execute(new Command.BleStartAdvertising { ServiceUuid = Service, Payload = new byte[] { 1 } });
        await advertising.NextCall();
        for (int queued = 0; queued < 64; queued++)
        {
            Assert.Equal(ExecuteResult.Accepted, shell.Execute(write));
        }
        Assert.Equal(ExecuteResult.Full, shell.Execute(write));

        Assert.Equal(ExecuteResult.Accepted, shell.Execute(new Command.BleStopAdvertising()));
        Assert.Equal(ExecuteResult.Inactive, shell.Execute(write));
    }

    [Fact]
    public async Task ADialReachesTheCentralPortAndOutboundRequestsArriveInOrder()
    {
        var central = new FakeCentral();
        await using var shell = new HardwareShell(new Backends { Central = central }, new DeviceCapabilities());

        Assert.Equal(ExecuteResult.Accepted, shell.Execute(new Command.BleConnect { DeviceId = "peer-1" }));
        Call<Dial> dial = await central.NextCall();
        shell.Execute(Write("peer-1", BleLinkDirection.Outbound, new byte[] { 5, 6 }));
        shell.Execute(new Command.BleReadCharacteristic
        {
            DeviceId = "peer-1", Direction = BleLinkDirection.Outbound, Uuid = "c2",
        });

        Assert.Equal("peer-1", dial.Arguments.DeviceId);
        var write = Assert.IsType<LinkRequest.Write>(
            await dial.Arguments.Requests.ReadAsync().AsTask().WaitAsync(Wait.Limit));
        var read = Assert.IsType<LinkRequest.Read>(
            await dial.Arguments.Requests.ReadAsync().AsTask().WaitAsync(Wait.Limit));
        Assert.Equal("c1", write.Uuid);
        Assert.Equal(new byte[] { 5, 6 }, write.Data);
        Assert.Equal("c2", read.Uuid);
    }

    [Fact]
    public async Task RequestsAndDisconnectsForAnotherPeerAreInactiveAndLeaveTheLinkUp()
    {
        var central = new FakeCentral();
        await using var shell = new HardwareShell(new Backends { Central = central }, new DeviceCapabilities());
        shell.Execute(new Command.BleConnect { DeviceId = "peer-1" });
        Call<Dial> dial = await central.NextCall();
        var disconnectOther = new Command.BleDisconnect { DeviceId = "peer-2", Direction = BleLinkDirection.Outbound };
        var disconnect = new Command.BleDisconnect { DeviceId = "peer-1", Direction = BleLinkDirection.Outbound };

        Assert.Equal(ExecuteResult.Inactive, shell.Execute(Write("peer-2", BleLinkDirection.Outbound, new byte[] { 1 })));
        Assert.Equal(ExecuteResult.Inactive, shell.Execute(disconnectOther));
        Assert.False(dial.Stop.IsCancellationRequested);

        Assert.Equal(ExecuteResult.Accepted, shell.Execute(disconnect));
        await dial.Stopped;
        Assert.Equal(ExecuteResult.Inactive, shell.Execute(disconnect));
    }

    [Theory]
    [InlineData(512, ExecuteResult.Accepted)]
    [InlineData(513, ExecuteResult.TooLarge)]
    public async Task AnAttributeValueLongerThanAttCarriesIsRefusedBeforeThePortSeesIt(int length, ExecuteResult expected)
    {
        var central = new FakeCentral();
        await using var shell = new HardwareShell(new Backends { Central = central }, new DeviceCapabilities());
        shell.Execute(new Command.BleConnect { DeviceId = "peer-1" });
        Call<Dial> dial = await central.NextCall();

        Assert.Equal(expected, shell.Execute(Write("peer-1", BleLinkDirection.Outbound, new byte[length])));

        Assert.Equal(expected == ExecuteResult.Accepted, dial.Arguments.Requests.TryRead(out _));
    }

    [Fact]
    public async Task AnIdentifierLongerThanTheBoundNeverReachesAPort()
    {
        var discovery = new FakeDiscovery();
        var central = new FakeCentral();
        await using var shell = new HardwareShell(
            new Backends { Discovery = discovery, Central = central },
            new DeviceCapabilities());
        string tooLong = new('x', 129);

        Assert.Equal(ExecuteResult.TooLarge, shell.Execute(new Command.BleStartScanning { ServiceUuid = tooLong }));
        Assert.Equal(ExecuteResult.TooLarge, shell.Execute(new Command.BleConnect { DeviceId = tooLong }));

        Assert.False(discovery.HasCall);
        Assert.False(central.HasCall);
    }
}
