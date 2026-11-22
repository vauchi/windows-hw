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
/// The central port over GATT, against a scripted peer: CI has no radio,
/// and what the port reports, in which order, is the part that can be
/// wrong without one. The events are those of <c>linux-hw</c>'s central.
/// </summary>
public class GattCentralTests
{
    private const string Peer = "207374182715392";
    private const ulong PeerAddress = 207374182715392;
    private const string Payload = "a1b2c3d4-e5f6-7890-abcd-ef1234567891";
    private const string Inbox = "a1b2c3d4-e5f6-7890-abcd-ef1234567892";

    /// <summary>A peer's GATT server whose characteristics, answers and failures the test sets.</summary>
    private sealed class FakeGatt : IGattDialer, IGattLink
    {
        private readonly TaskCompletionSource<IGattSignals> _dialed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HardwareFailure? DialFailure { get; set; }

        public List<ulong> Dialed { get; } = new();

        public List<string> Subscribed { get; } = new();

        public HashSet<string> RefusesSubscription { get; } = new();

        public HashSet<string> RefusesWrites { get; } = new();

        public Dictionary<string, byte[]> Values { get; } = new();

        public List<(string Uuid, byte[] Data)> Written { get; } = new();

        public uint? Mtu { get; set; }

        public IReadOnlyList<GattCharacteristic> Characteristics { get; set; } = new[]
        {
            new GattCharacteristic(Payload, Notifies: true),
            new GattCharacteristic(Inbox, Notifies: false),
        };

        /// <summary>The peer's side of the link, once the port has dialled.</summary>
        public Task<IGattSignals> Signals => _dialed.Task.WaitAsync(Wait.Limit);

        /// <summary>Completes when the port let go of the link.</summary>
        public Task Released => _released.Task.WaitAsync(Wait.Limit);

        public bool IsReleased => _released.Task.IsCompleted;

        public Task<IGattLink> DialAsync(ulong address, IGattSignals signals, CancellationToken stop)
        {
            Dialed.Add(address);
            if (DialFailure is { } failure)
            {
                throw new HardwareFailureException(failure);
            }
            _dialed.TrySetResult(signals);
            return Task.FromResult<IGattLink>(this);
        }

        public Task<bool> SubscribeAsync(string uuid, CancellationToken stop)
        {
            Subscribed.Add(uuid);
            return Task.FromResult(!RefusesSubscription.Contains(uuid));
        }

        public Task<bool> WriteAsync(string uuid, byte[] data, CancellationToken stop)
        {
            Written.Add((uuid, data));
            return Task.FromResult(!RefusesWrites.Contains(uuid));
        }

        public Task<byte[]?> ReadAsync(string uuid, CancellationToken stop) =>
            Task.FromResult(Values.GetValueOrDefault(uuid));

        public ValueTask DisposeAsync()
        {
            _released.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private static HardwareShell ShellOver(FakeGatt gatt) =>
        new(new Backends { Central = new GattCentral(gatt) }, new DeviceCapabilities());

    private static Command Write(string uuid, params byte[] data) => new Command.BleWriteCharacteristic
    {
        DeviceId = Peer, Direction = BleLinkDirection.Outbound, Uuid = uuid, Data = data,
    };

    private static Command Read(string uuid) => new Command.BleReadCharacteristic
    {
        DeviceId = Peer, Direction = BleLinkDirection.Outbound, Uuid = uuid,
    };

    private static string Connected => new Event.BleConnected
    {
        DeviceId = Peer, Direction = BleLinkDirection.Outbound,
    }.Wire();

    private static string Error(string text) => new Event.HardwareError { Transport = "BLE", Error = text }.Wire();

    private static async Task<FakeGatt> Linked(HardwareShell shell, FakeGatt gatt)
    {
        Assert.Equal(ExecuteResult.Accepted, shell.Execute(new Command.BleConnect { DeviceId = Peer }));
        Assert.Equal(Connected, (await shell.Next()).Wire());
        return gatt;
    }

    [Fact]
    public async Task ADialSubscribesEveryNotifyingCharacteristicThenReportsTheMtuAndTheConnection()
    {
        var gatt = new FakeGatt { Mtu = 247 };
        await using HardwareShell shell = ShellOver(gatt);

        Assert.Equal(ExecuteResult.Accepted, shell.Execute(new Command.BleConnect { DeviceId = Peer }));
        Event first = await shell.Next();
        Event second = await shell.Next();

        Assert.Equal(new[] { PeerAddress }, gatt.Dialed);
        Assert.Equal(new[] { Payload }, gatt.Subscribed);
        Assert.Equal(new Event.BleMtuNegotiated { DeviceId = Peer, Mtu = 247 }.Wire(), first.Wire());
        Assert.Equal(Connected, second.Wire());
    }

    [Fact]
    public async Task ALinkWhoseMtuIsNotKnownReportsTheConnectionAlone()
    {
        var gatt = new FakeGatt();
        await using HardwareShell shell = ShellOver(gatt);

        shell.Execute(new Command.BleConnect { DeviceId = Peer });

        Assert.Equal(Connected, (await shell.Next()).Wire());
    }

    [Theory]
    [InlineData("")]
    [InlineData("peer-1")]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData(" 5")]
    [InlineData("5 ")]
    [InlineData("1.5")]
    [InlineData("0x10")]
    [InlineData("007")]
    [InlineData("AA:BB:CC:DD:EE:FF")]
    [InlineData("١٢")]
    [InlineData("281474976710656")]
    [InlineData("99999999999999999999999")]
    public async Task ADeviceIdThatIsNotAnAddressFailsTheLinkWithoutDialling(string deviceId)
    {
        var gatt = new FakeGatt();
        await using HardwareShell shell = ShellOver(gatt);

        Assert.Equal(ExecuteResult.Accepted, shell.Execute(new Command.BleConnect { DeviceId = deviceId }));

        Assert.Equal(Error("Bluetooth link failed"), (await shell.Next()).Wire());
        Assert.Empty(gatt.Dialed);
    }

    [Theory]
    [InlineData("0", 0UL)]
    [InlineData("281474976710655", 281474976710655UL)]
    public async Task EveryAddressBluetoothHasIsDialled(string deviceId, ulong address)
    {
        var gatt = new FakeGatt();
        await using HardwareShell shell = ShellOver(gatt);

        shell.Execute(new Command.BleConnect { DeviceId = deviceId });
        await shell.Next();

        Assert.Equal(new[] { address }, gatt.Dialed);
    }

    [Theory]
    [InlineData(HardwareFailure.Unavailable)]
    [InlineData(HardwareFailure.PermissionDenied)]
    public async Task WhatTheOsSaysAboutADialIsWhatCoreHears(HardwareFailure failure)
    {
        var gatt = new FakeGatt { DialFailure = failure };
        await using HardwareShell shell = ShellOver(gatt);
        Event expected = failure == HardwareFailure.Unavailable
            ? new Event.HardwareUnavailable { Transport = "BLE" }
            : new Event.PermissionDenied { Transport = "BLE" };

        shell.Execute(new Command.BleConnect { DeviceId = Peer });

        Assert.Equal(expected.Wire(), (await shell.Next()).Wire());
    }

    [Fact]
    public async Task ARefusedSubscriptionFailsTheLinkBeforeItIsReportedAndReleasesIt()
    {
        var gatt = new FakeGatt();
        gatt.RefusesSubscription.Add(Payload);
        await using HardwareShell shell = ShellOver(gatt);

        shell.Execute(new Command.BleConnect { DeviceId = Peer });

        Assert.Equal(Error("Bluetooth link failed"), (await shell.Next()).Wire());
        await gatt.Released;
    }

    [Fact]
    public async Task AWriteReachesItsCharacteristicWhateverCaseCoreSpelledItIn()
    {
        var gatt = new FakeGatt();
        await using HardwareShell shell = ShellOver(gatt);
        await Linked(shell, gatt);
        gatt.Values[Payload] = new byte[] { 9 };

        shell.Execute(Write(Inbox.ToUpperInvariant(), 1, 2, 3));
        shell.Execute(Read(Payload));
        Event answer = await shell.Next();

        (string uuid, byte[] data) = Assert.Single(gatt.Written);
        Assert.Equal(Inbox, uuid);
        Assert.Equal(new byte[] { 1, 2, 3 }, data);
        Assert.Equal(
            new Event.BleCharacteristicRead
            {
                DeviceId = Peer, Direction = BleLinkDirection.Outbound, Uuid = Payload, Data = new byte[] { 9 },
            }.Wire(),
            answer.Wire());
    }

    [Fact]
    public async Task AReadAnswersWithTheUuidAsCoreSpelledIt()
    {
        var gatt = new FakeGatt();
        await using HardwareShell shell = ShellOver(gatt);
        await Linked(shell, gatt);
        gatt.Values[Payload] = new byte[] { 7 };

        shell.Execute(Read(Payload.ToUpperInvariant()));

        var read = Assert.IsType<Event.BleCharacteristicRead>(await shell.Next());
        Assert.Equal(Payload.ToUpperInvariant(), read.Uuid);
        Assert.Equal(new byte[] { 7 }, read.Data);
    }

    [Fact]
    public async Task RequestsThePeerCannotServeAreErrorsAndLeaveTheLinkUp()
    {
        var gatt = new FakeGatt();
        gatt.RefusesWrites.Add(Inbox);
        await using HardwareShell shell = ShellOver(gatt);
        await Linked(shell, gatt);

        shell.Execute(Write("a1b2c3d4-e5f6-7890-abcd-ef12345678ff", 1));
        Event unknownWrite = await shell.Next();
        shell.Execute(Write(Inbox, 1));
        Event refusedWrite = await shell.Next();
        shell.Execute(Read("a1b2c3d4-e5f6-7890-abcd-ef12345678ff"));
        Event unknownRead = await shell.Next();
        shell.Execute(Read(Inbox));
        Event failedRead = await shell.Next();

        Assert.Equal(Error("Bluetooth characteristic not found"), unknownWrite.Wire());
        Assert.Equal(Error("Bluetooth write failed"), refusedWrite.Wire());
        Assert.Equal(Error("Bluetooth read failed"), unknownRead.Wire());
        Assert.Equal(Error("Bluetooth read failed"), failedRead.Wire());
        Assert.False(gatt.IsReleased);
    }

    [Fact]
    public async Task WhatThePeerNotifiesIsReportedOnTheOutboundLinkInOrder()
    {
        var gatt = new FakeGatt();
        await using HardwareShell shell = ShellOver(gatt);
        await Linked(shell, gatt);
        IGattSignals peer = await gatt.Signals;

        peer.Notified(Payload, new byte[] { 1 });
        peer.Notified(Payload, new byte[] { 2 });
        var first = Assert.IsType<Event.BleCharacteristicNotified>(await shell.Next());
        var second = Assert.IsType<Event.BleCharacteristicNotified>(await shell.Next());

        Assert.Equal((Peer, BleLinkDirection.Outbound, Payload), (first.DeviceId, first.Direction, first.Uuid));
        Assert.Equal(new byte[] { 1 }, first.Data);
        Assert.Equal(new byte[] { 2 }, second.Data);
    }

    [Fact]
    public async Task AnMtuNegotiatedLaterIsReportedWhenItChanges()
    {
        var gatt = new FakeGatt();
        await using HardwareShell shell = ShellOver(gatt);
        await Linked(shell, gatt);

        (await gatt.Signals).MtuChanged(185);

        Assert.Equal(new Event.BleMtuNegotiated { DeviceId = Peer, Mtu = 185 }.Wire(), (await shell.Next()).Wire());
    }

    [Fact]
    public async Task ANotificationLongerThanAttCarriesIsNotReportedAndTheLinkStaysUp()
    {
        var gatt = new FakeGatt();
        await using HardwareShell shell = ShellOver(gatt);
        await Linked(shell, gatt);
        IGattSignals peer = await gatt.Signals;

        peer.Notified(Payload, new byte[513]);
        peer.Notified(Payload, new byte[512]);

        var kept = Assert.IsType<Event.BleCharacteristicNotified>(await shell.Next());
        Assert.Equal(512, kept.Data.Length);
        Assert.False(gatt.IsReleased);
    }

    [Fact]
    public async Task ALostPeerIsReportedOnceAndTheLinkIsReleased()
    {
        var gatt = new FakeGatt();
        await using HardwareShell shell = ShellOver(gatt);
        await Linked(shell, gatt);
        IGattSignals peer = await gatt.Signals;

        peer.Lost();
        peer.Lost();
        Event lost = await shell.Next();
        await gatt.Released;

        Assert.Equal(
            new Event.BleDisconnected
            {
                DeviceId = Peer, Direction = BleLinkDirection.Outbound, Reason = "the peer disconnected",
            }.Wire(),
            lost.Wire());
        Assert.False(shell.TryTake(out _));
    }

    [Fact]
    public async Task CoresDisconnectReleasesTheLinkAndReportsNothing()
    {
        var gatt = new FakeGatt();
        await using HardwareShell shell = ShellOver(gatt);
        await Linked(shell, gatt);
        var disconnect = new Command.BleDisconnect { DeviceId = Peer, Direction = BleLinkDirection.Outbound };

        Assert.Equal(ExecuteResult.Accepted, shell.Execute(disconnect));
        await gatt.Released;

        Assert.False(shell.TryTake(out _));
    }

    [Fact]
    public async Task APeerThatNotifiesFasterThanCoreTakesEndsTheLinkInsteadOfLosingAChunk()
    {
        var gatt = new FakeGatt();
        await using HardwareShell shell = ShellOver(gatt);
        await Linked(shell, gatt);
        IGattSignals peer = await gatt.Signals;
        const int Burst = 5000;

        for (int sent = 0; sent < Burst; sent++)
        {
            peer.Notified(Payload, new[] { (byte)sent });
        }

        var reported = new List<Event>();
        do
        {
            reported.Add(await shell.Next());
        }
        while (reported[^1] is Event.BleCharacteristicNotified && reported.Count <= Burst);
        await gatt.Released;

        Assert.Equal(Error("Bluetooth link failed"), reported[^1].Wire());
        byte[] chunks = reported.Take(reported.Count - 1)
            .Select(notified => Assert.IsType<Event.BleCharacteristicNotified>(notified).Data[0]).ToArray();
        Assert.InRange(chunks.Length, 1, Burst - 1);
        Assert.Equal(Enumerable.Range(0, chunks.Length).Select(index => (byte)index), chunks);
    }
}
