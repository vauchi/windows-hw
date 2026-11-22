// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vauchi.Contract;
using Xunit;

namespace Vauchi.WindowsHw.Tests;

/// <summary>
/// The peripheral port over a GATT server, against a scripted host: CI
/// has no radio. What the service looks like and what a central's reads,
/// writes and subscriptions report are those of <c>linux-hw</c>'s
/// peripheral (<c>src/bluez/peripheral.rs</c>).
/// </summary>
public class GattPeripheralTests
{
    private const string Service = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string Payload = "a1b2c3d4-e5f6-7890-abcd-ef1234567891";
    private const string Inbox = "a1b2c3d4-e5f6-7890-abcd-ef1234567892";
    private const string WriteOnly = "a1b2c3d4-e5f6-7890-abcd-ef1234567894";
    private const string NotifyOnly = "a1b2c3d4-e5f6-7890-abcd-ef1234567897";
    private const string Phone = "207374182715392";

    /// <summary>The OS side of a published service: what was asked of it and a handle on the port's callbacks.</summary>
    private sealed class FakeHost : IGattServerHost, IGattServer
    {
        private readonly TaskCompletionSource<IGattCentrals> _published = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _withdrawn = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _notified = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HardwareFailure? PublishFailure { get; set; }

        public GattServerLayout? Layout { get; private set; }

        public List<(string Uuid, byte[] Data)> Notifications { get; } = new();

        /// <summary>The centrals' side, once the port has published.</summary>
        public Task<IGattCentrals> Centrals => _published.Task.WaitAsync(Wait.Limit);

        public Task Withdrawn => _withdrawn.Task.WaitAsync(Wait.Limit);

        public Task Notified => _notified.Task.WaitAsync(Wait.Limit);

        public Task<IGattServer> PublishAsync(GattServerLayout layout, IGattCentrals centrals, CancellationToken stop)
        {
            if (PublishFailure is { } failure)
            {
                throw new HardwareFailureException(failure);
            }
            Layout = layout;
            _published.TrySetResult(centrals);
            return Task.FromResult<IGattServer>(this);
        }

        public Task NotifyAsync(string uuid, byte[] data, CancellationToken stop)
        {
            Notifications.Add((uuid, data));
            _notified.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _withdrawn.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private static HardwareShell ShellOver(FakeHost host) =>
        new(new Backends { Advertising = new GattPeripheral(host) }, new DeviceCapabilities());

    private static Command Advertise(params byte[] payload) =>
        new Command.BleStartAdvertising { ServiceUuid = Service, Payload = payload };

    private static Command Notify(string uuid, params byte[] data) => new Command.BleWriteCharacteristic
    {
        DeviceId = "", Direction = BleLinkDirection.Inbound, Uuid = uuid, Data = data,
    };

    private static string Error(string text) => new Event.HardwareError { Transport = "BLE", Error = text }.Wire();

    private static async Task<IGattCentrals> Published(HardwareShell shell, FakeHost host, params byte[] payload)
    {
        Assert.Equal(ExecuteResult.Accepted, shell.Execute(Advertise(payload)));
        return await host.Centrals;
    }

    [Fact]
    public async Task TheServiceIsTheOneThePhonesExposeAndTheTokenIsAdvertisedBesideIt()
    {
        var host = new FakeHost();
        await using HardwareShell shell = ShellOver(host);

        await Published(shell, host, 0xAB, 0xCD, 0xEF);

        GattServerLayout layout = Assert.IsType<GattServerLayout>(host.Layout);
        Assert.Equal(Service, layout.ServiceUuid);
        Assert.Equal("0000abcd-0000-1000-8000-00805f9b34fb", layout.TokenServiceUuid);
        Assert.Equal(
            new[]
            {
                new GattServerCharacteristic(Payload, GattAccess.Read | GattAccess.Notify),
                new GattServerCharacteristic(Inbox, GattAccess.Write | GattAccess.Notify),
                new GattServerCharacteristic("a1b2c3d4-e5f6-7890-abcd-ef1234567893", GattAccess.Write | GattAccess.Notify),
                new GattServerCharacteristic(WriteOnly, GattAccess.Write),
                new GattServerCharacteristic("a1b2c3d4-e5f6-7890-abcd-ef1234567895", GattAccess.Write | GattAccess.Notify),
                new GattServerCharacteristic("a1b2c3d4-e5f6-7890-abcd-ef1234567896", GattAccess.WriteWithoutResponse),
                new GattServerCharacteristic(NotifyOnly, GattAccess.Notify),
            },
            layout.Characteristics);
    }

    [Theory]
    [InlineData(new byte[0], "00000000-0000-1000-8000-00805f9b34fb")]
    [InlineData(new byte[] { 0x7F }, "00007f00-0000-1000-8000-00805f9b34fb")]
    public async Task ATokenShorterThanTwoBytesIsPaddedWithZeros(byte[] payload, string token)
    {
        var host = new FakeHost();
        await using HardwareShell shell = ShellOver(host);

        await Published(shell, host, payload);

        Assert.Equal(token, host.Layout!.TokenServiceUuid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    [InlineData("A1B2C3D4E5F67890ABCDEF1234567890")]
    [InlineData(" a1b2c3d4-e5f6-7890-abcd-ef1234567890")]
    public async Task AServiceUuidThatIsNotCanonicalFailsWithoutPublishing(string serviceUuid)
    {
        var host = new FakeHost();
        await using HardwareShell shell = ShellOver(host);

        shell.Execute(new Command.BleStartAdvertising { ServiceUuid = serviceUuid, Payload = new byte[] { 1, 2 } });

        Assert.Equal(Error("Bluetooth advertising failed"), (await shell.Next()).Wire());
        Assert.Null(host.Layout);
    }

    [Theory]
    [InlineData(HardwareFailure.Unavailable)]
    [InlineData(HardwareFailure.PermissionDenied)]
    public async Task WhatTheOsSaysAboutPublishingIsWhatCoreHears(HardwareFailure failure)
    {
        var host = new FakeHost { PublishFailure = failure };
        await using HardwareShell shell = ShellOver(host);
        Event expected = failure == HardwareFailure.Unavailable
            ? new Event.HardwareUnavailable { Transport = "BLE" }
            : new Event.PermissionDenied { Transport = "BLE" };

        shell.Execute(Advertise(1, 2));

        Assert.Equal(expected.Wire(), (await shell.Next()).Wire());
    }

    [Fact]
    public async Task OnlyThePayloadCharacteristicHasAValueAndAReadPastItsEndIsRefused()
    {
        var host = new FakeHost();
        await using HardwareShell shell = ShellOver(host);
        IGattCentrals centrals = await Published(shell, host, 1, 2, 3);

        Assert.Equal(new byte[] { 1, 2, 3 }, centrals.Read(Payload, 0));
        Assert.Equal(new byte[] { 3 }, centrals.Read(Payload.ToUpperInvariant(), 2));
        Assert.Equal(Array.Empty<byte>(), centrals.Read(Payload, 3));
        Assert.Null(centrals.Read(Payload, 4));
        Assert.Null(centrals.Read(Payload, -1));
        Assert.Equal(Array.Empty<byte>(), centrals.Read(Inbox, 0));
        Assert.Null(centrals.Read(Inbox, 1));
        Assert.Null(centrals.Read("a1b2c3d4-e5f6-7890-abcd-ef12345678ff", 0));
    }

    [Fact]
    public async Task TheFirstSubscriptionIsTheConnectionAndTheLastOneGoneIsTheDisconnection()
    {
        var host = new FakeHost();
        await using HardwareShell shell = ShellOver(host);
        IGattCentrals centrals = await Published(shell, host, 1, 2);

        await centrals.SubscriptionChangedAsync(Payload, subscribed: true);
        await centrals.SubscriptionChangedAsync(Inbox, subscribed: true);
        await centrals.SubscriptionChangedAsync(Inbox, subscribed: true);
        await centrals.SubscriptionChangedAsync(Payload, subscribed: false);
        await centrals.SubscriptionChangedAsync(WriteOnly, subscribed: false);
        await centrals.SubscriptionChangedAsync(Inbox, subscribed: false);
        var reported = new[] { await shell.Next(), await shell.Next(), await shell.Next() };

        Assert.Equal(
            new[]
            {
                new Event.BleConnected { DeviceId = "", Direction = BleLinkDirection.Inbound }.Wire(),
                new Event.BleGattReady { DeviceId = "" }.Wire(),
                new Event.BleDisconnected
                {
                    DeviceId = "", Direction = BleLinkDirection.Inbound, Reason = "central unsubscribed",
                }.Wire(),
            },
            reported.Select(observation => observation.Wire()));
        await centrals.SubscriptionChangedAsync(Payload, subscribed: true);
        Assert.IsType<Event.BleConnected>(await shell.Next());
    }

    [Fact]
    public async Task ACentralsWriteIsReportedOnTheInboundLinkWithItsMtuOnceWhenItChanges()
    {
        var host = new FakeHost();
        await using HardwareShell shell = ShellOver(host);
        IGattCentrals centrals = await Published(shell, host, 1, 2);

        Assert.True(await centrals.WrittenAsync(Phone, Inbox.ToUpperInvariant(), new byte[] { 5 }, 247));
        Assert.True(await centrals.WrittenAsync(Phone, Inbox, new byte[] { 6 }, 247));
        Assert.True(await centrals.WrittenAsync(Phone, Inbox, new byte[] { 7 }, null));
        var reported = new[] { await shell.Next(), await shell.Next(), await shell.Next(), await shell.Next() };

        Assert.Equal(new Event.BleMtuNegotiated { DeviceId = Phone, Mtu = 247 }.Wire(), reported[0].Wire());
        Assert.Equal(
            new Event.BleCharacteristicNotified
            {
                DeviceId = Phone, Direction = BleLinkDirection.Inbound, Uuid = Inbox, Data = new byte[] { 5 },
            }.Wire(),
            reported[1].Wire());
        Assert.Equal(new byte[] { 6 }, Assert.IsType<Event.BleCharacteristicNotified>(reported[2]).Data);
        Assert.Equal(new byte[] { 7 }, Assert.IsType<Event.BleCharacteristicNotified>(reported[3]).Data);
    }

    [Fact]
    public async Task WritesTheServiceDoesNotTakeAreRefusedAndReportNothing()
    {
        var host = new FakeHost();
        await using HardwareShell shell = ShellOver(host);
        IGattCentrals centrals = await Published(shell, host, 1, 2);

        Assert.False(await centrals.WrittenAsync(Phone, Inbox, new byte[513], null));
        Assert.False(await centrals.WrittenAsync(Phone, Payload, new byte[] { 1 }, null));
        Assert.False(await centrals.WrittenAsync(Phone, NotifyOnly, new byte[] { 1 }, null));
        Assert.False(await centrals.WrittenAsync(Phone, "a1b2c3d4-e5f6-7890-abcd-ef12345678ff", new byte[] { 1 }, null));
        Assert.True(await centrals.WrittenAsync(Phone, Inbox, new byte[512], null));

        Assert.Equal(512, Assert.IsType<Event.BleCharacteristicNotified>(await shell.Next()).Data.Length);
    }

    [Fact]
    public async Task ACentralWhoseIdIsLongerThanTheContractCarriesIsReportedUnaddressed()
    {
        var host = new FakeHost();
        await using HardwareShell shell = ShellOver(host);
        IGattCentrals centrals = await Published(shell, host, 1, 2);

        Assert.True(await centrals.WrittenAsync(new string('9', 129), Inbox, new byte[] { 1 }, null));

        Assert.Equal("", Assert.IsType<Event.BleCharacteristicNotified>(await shell.Next()).DeviceId);
    }

    [Fact]
    public async Task CoresNotificationsReachTheNotifyingCharacteristicsAndNoOther()
    {
        var host = new FakeHost();
        await using HardwareShell shell = ShellOver(host);
        await Published(shell, host, 1, 2);

        Assert.Equal(ExecuteResult.Accepted, shell.Execute(Notify(WriteOnly, 1)));
        Assert.Equal(ExecuteResult.Accepted, shell.Execute(Notify("a1b2c3d4-e5f6-7890-abcd-ef12345678ff", 2)));
        Assert.Equal(ExecuteResult.Accepted, shell.Execute(Notify(NotifyOnly.ToUpperInvariant(), 3)));
        await host.Notified;

        (string uuid, byte[] data) = Assert.Single(host.Notifications);
        Assert.Equal(NotifyOnly, uuid);
        Assert.Equal(new byte[] { 3 }, data);
    }

    [Fact]
    public async Task StoppingWithdrawsTheServiceAndReportsNothing()
    {
        var host = new FakeHost();
        await using HardwareShell shell = ShellOver(host);
        await Published(shell, host, 1, 2);

        Assert.Equal(ExecuteResult.Accepted, shell.Execute(new Command.BleStopAdvertising()));
        await host.Withdrawn;

        var again = new FakeHost();
        await using HardwareShell next = ShellOver(again);
        IGattCentrals centrals = await Published(next, again, 1, 2);
        await centrals.SubscriptionChangedAsync(Payload, subscribed: true);
        Assert.IsType<Event.BleConnected>(await next.Next());
    }

    [Fact]
    public async Task AnAdvertisementTheOsWithdrawsIsAFailureAndTheServiceGoesWithIt()
    {
        var host = new FakeHost();
        await using HardwareShell shell = ShellOver(host);
        IGattCentrals centrals = await Published(shell, host, 1, 2);

        centrals.Withdrawn();

        Assert.Equal(Error("Bluetooth advertising failed"), (await shell.Next()).Wire());
        await host.Withdrawn;
    }

    [Theory]
    [InlineData("BluetoothLE#BluetoothLE00:11:22:33:44:55-bc:9a:78:56:34:12", "207371629900818")]
    [InlineData("BluetoothLE#BluetoothLE00:11:22:33:44:55-BC:9A:78:56:34:12", "207371629900818")]
    [InlineData("BluetoothLE#BluetoothLE00:11:22:33:44:55-00:00:00:00:00:00", "0")]
    [InlineData("", "")]
    [InlineData("bc:9a:78:56:34:12", "")]
    [InlineData("BluetoothLE#BluetoothLE00:11:22:33:44:55-bc:9a:78:56:34", "")]
    [InlineData("BluetoothLE#BluetoothLE00:11:22:33:44:55-bc:9a:78:56:34:1g", "")]
    [InlineData("BluetoothLE#BluetoothLE00:11:22:33:44:55-bc9a78563412abcde", "")]
    [InlineData("BluetoothLE#BluetoothLE00:11:22:33:44:55-bc:9a:78:56:34:12 ", "")]
    [InlineData("BluetoothLE#BluetoothLE00:11:22:33:44:55-+c:9a:78:56:34:12", "")]
    public void ACentralIsNamedByItsAddressAsDiscoveryNamesItOrNotAtAll(string osDeviceId, string expected)
    {
        Assert.Equal(expected, BleObservations.CentralId(osDeviceId));
    }

    [Fact]
    public async Task CentralsWritingAtOnceAreEachReportedWhole()
    {
        var host = new FakeHost();
        await using HardwareShell shell = ShellOver(host);
        IGattCentrals centrals = await Published(shell, host, 1, 2);
        const int Writers = 8;
        const int Each = 40;

        Task[] writing = Enumerable.Range(0, Writers).Select(writer => Task.Run(async () =>
        {
            for (int chunk = 0; chunk < Each; chunk++)
            {
                Assert.True(await centrals.WrittenAsync(
                    writer.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Inbox,
                    new[] { (byte)chunk },
                    null));
            }
        })).ToArray();
        var perWriter = new Dictionary<string, List<byte>>();
        for (int taken = 0; taken < Writers * Each; taken++)
        {
            var write = Assert.IsType<Event.BleCharacteristicNotified>(await shell.Next());
            if (!perWriter.TryGetValue(write.DeviceId, out List<byte>? chunks))
            {
                perWriter[write.DeviceId] = chunks = new List<byte>();
            }
            chunks.Add(write.Data[0]);
        }
        await Task.WhenAll(writing).WaitAsync(Wait.Limit);

        Assert.Equal(Writers, perWriter.Count);
        Assert.All(perWriter.Values, chunks => Assert.Equal(Enumerable.Range(0, Each).Select(chunk => (byte)chunk), chunks));
    }
}
