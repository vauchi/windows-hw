// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vauchi.Contract;
using Xunit;

namespace Vauchi.WindowsHw.Tests;

public class ShellTests
{
    private static readonly DeviceCapabilities Everything = new()
    {
        HasBle = true, HasNfc = true, HasCamera = true, HasInternet = true, Platform = Platform.Desktop,
    };

    private static Backends AllBackends() => new()
    {
        Discovery = new FakeDiscovery(),
        Advertising = new FakeAdvertising(),
        Central = new FakeCentral(),
        Nfc = new FakeReader(),
        Camera = new FakeCamera(),
        Direct = new FakeDirect((_, payload, _) => payload),
    };

    private static DeviceCapabilities Reported(HardwareShell shell) =>
        Assert.IsType<Event.HardwareCapabilitiesReported>(shell.Report()).Capabilities;

    private static Command Wakeup(uint delayMillis) => new Command.ScheduleWakeup
    {
        EarliestSecs = 0, DeadlineSecs = 1, MinIntervalSecs = 0, DelayMillis = delayMillis,
    };

    [Fact]
    public async Task TheReportClaimsWhatTheDeviceHasAndTheBackendsRun()
    {
        await using var shell = new HardwareShell(AllBackends(), Everything);

        DeviceCapabilities reported = Reported(shell);

        Assert.True(reported.HasBle);
        Assert.True(reported.HasNfc);
        Assert.True(reported.HasCamera);
        Assert.True(reported.HasInternet);
        Assert.Equal(Platform.Desktop, reported.Platform);
        Assert.Equal(AudioCapability.None, reported.Audio);
    }

    [Fact]
    public async Task TheReportNeverClaimsADeviceWhoseBackendIsMissing()
    {
        await using var none = new HardwareShell(new Backends(), Everything);
        await using var centralOnly = new HardwareShell(new Backends { Central = new FakeCentral() }, Everything);

        Assert.False(Reported(none).HasBle);
        Assert.False(Reported(none).HasNfc);
        Assert.False(Reported(none).HasCamera);
        Assert.True(Reported(none).HasInternet);
        Assert.False(Reported(centralOnly).HasBle);
    }

    [Fact]
    public async Task TheReportNeverClaimsADeviceTheHostDoesNotHave()
    {
        await using var shell = new HardwareShell(AllBackends(), new DeviceCapabilities());

        Assert.False(Reported(shell).HasBle);
        Assert.False(Reported(shell).HasNfc);
        Assert.False(Reported(shell).HasCamera);
    }

    public static TheoryData<Command> WithoutAPort => new()
    {
        new Command.BleStartScanning { ServiceUuid = "s" },
        new Command.BleStopScanning(),
        new Command.BleStartAdvertising { ServiceUuid = "s", Payload = new byte[] { 1 } },
        new Command.BleConnect { DeviceId = "p" },
        new Command.NfcActivate { Payload = Array.Empty<byte>() },
        new Command.NfcDeactivate(),
        new Command.CameraCapture { PreviewId = "v", Purpose = CameraPurpose.QrScan },
        new Command.CameraStop { PreviewId = "v" },
        new Command.DirectSend { Payload = new byte[] { 1 }, IsInitiator = true },
        new Command.AudioStop(),
        new Command.AccelerometerStart(),
        new Command.QrRequestScan(),
        new Command.QrDisplay { Data = "display command" },
    };

    [Theory]
    [MemberData(nameof(WithoutAPort))]
    public async Task ACommandWithNoPortIsRefusedToTheCallerAndReportsNothing(Command command)
    {
        await using var shell = new HardwareShell(new Backends(), Everything);

        Assert.Equal(ExecuteResult.Unsupported, shell.Execute(command));
        Assert.False(shell.TryTake(out Event? observation));
        Assert.Null(observation);
    }

    [Fact]
    public async Task LinksTheCentralPortDoesNotCarryAreRefusedEvenWithEveryBackend()
    {
        await using var shell = new HardwareShell(AllBackends(), Everything);

        Assert.Equal(ExecuteResult.Unsupported, shell.Execute(new Command.BleReadCharacteristic
        {
            DeviceId = "p", Direction = BleLinkDirection.Inbound, Uuid = "c",
        }));
        Assert.Equal(ExecuteResult.Unsupported, shell.Execute(new Command.BleDisconnect
        {
            DeviceId = "p", Direction = BleLinkDirection.Inbound,
        }));
    }

    [Fact]
    public async Task ADirectExchangeReportsThePeersPayloadWithCoresEndpointAndRole()
    {
        (DirectEndpoint Endpoint, byte[] Payload, bool IsInitiator)? seen = null;
        var direct = new FakeDirect((endpoint, payload, isInitiator) =>
        {
            seen = (endpoint, payload, isInitiator);
            return new byte[] { 4, 5, 6 };
        });
        await using var shell = new HardwareShell(new Backends { Direct = direct }, Everything);
        var send = new Command.DirectSend { Payload = new byte[] { 1, 2 }, IsInitiator = true };

        Assert.Equal(ExecuteResult.Accepted, shell.Execute(send));

        Assert.Equal("{\"DirectPayloadReceived\":{\"data\":[4,5,6]}}", (await shell.Next()).Wire());
        Assert.Equal(new byte[] { 1, 2 }, seen!.Value.Payload);
        Assert.True(seen.Value.IsInitiator);
        Assert.Equal((ushort)19283, seen.Value.Endpoint.Port);
    }

    [Fact]
    public async Task AFailedDirectExchangeIsAHardwareErrorOnTheTransportShellsAlwaysNamed()
    {
        var direct = new FakeDirect((_, _, _) => throw new DirectExchangeException("TCP connect failed: ConnectionRefused"));
        await using var shell = new HardwareShell(new Backends { Direct = direct }, Everything);

        shell.Execute(new Command.DirectSend { Payload = new byte[] { 1 }, IsInitiator = false });

        Assert.Equal(
            "{\"HardwareError\":{\"error\":\"TCP connect failed: ConnectionRefused\",\"transport\":\"USB\"}}",
            (await shell.Next()).Wire());
    }

    [Fact]
    public async Task ADirectFailureThatIsNotThePortsOwnIsReportedWithoutItsText()
    {
        var direct = new FakeDirect((_, _, _) => throw new InvalidOperationException("192.168.1.20:19283"));
        await using var shell = new HardwareShell(new Backends { Direct = direct }, Everything);

        shell.Execute(new Command.DirectSend { Payload = new byte[] { 1 }, IsInitiator = false });

        Assert.Equal(
            "{\"HardwareError\":{\"error\":\"direct exchange failed\",\"transport\":\"USB\"}}",
            (await shell.Next()).Wire());
    }

    [Theory]
    [InlineData(0, ExecuteResult.TooLarge)]
    [InlineData(1, ExecuteResult.Accepted)]
    [InlineData(DirectFrame.MaxPayload, ExecuteResult.Accepted)]
    [InlineData(DirectFrame.MaxPayload + 1, ExecuteResult.TooLarge)]
    public async Task ADirectPayloadOutsideTheFramesBoundsNeverReachesThePort(int length, ExecuteResult expected)
    {
        int exchanges = 0;
        var direct = new FakeDirect((_, payload, _) =>
        {
            Interlocked.Increment(ref exchanges);
            return payload;
        });
        var shell = new HardwareShell(new Backends { Direct = direct }, Everything);

        Assert.Equal(expected, shell.Execute(new Command.DirectSend { Payload = new byte[length], IsInitiator = true }));

        await shell.DisposeAsync();
        Assert.Equal(expected == ExecuteResult.Accepted ? 1 : 0, Volatile.Read(ref exchanges));
    }

    [Fact]
    public async Task TheLargestPayloadAFrameCarriesFitsTheInboxAsAnEvent()
    {
        byte[] largest = Enumerable.Repeat((byte)255, DirectFrame.MaxPayload).ToArray();
        await using var shell = new HardwareShell(new Backends { Direct = new FakeDirect((_, _, _) => largest) }, Everything);

        shell.Execute(new Command.DirectSend { Payload = new byte[] { 1 }, IsInitiator = true });

        var received = Assert.IsType<Event.DirectPayloadReceived>(await shell.Next());
        Assert.Equal(largest, received.Data);
    }

    [Fact]
    public async Task AnExchangesAnswerSurvivesTheNextExchangeStarting()
    {
        byte answer = 0;
        var direct = new FakeDirect((_, _, _) => new[] { ++answer });
        await using var shell = new HardwareShell(new Backends { Direct = direct }, Everything);
        var send = new Command.DirectSend { Payload = new byte[] { 1 }, IsInitiator = true };
        shell.Execute(send);
        Event first = await shell.Next();
        shell.Execute(send);
        Event second = await shell.Next();

        Assert.Equal("{\"DirectPayloadReceived\":{\"data\":[1]}}", first.Wire());
        Assert.Equal("{\"DirectPayloadReceived\":{\"data\":[2]}}", second.Wire());
    }

    [Fact]
    public async Task AWakeupFiresOnceWhenItsDelayHasPassedAndNotBefore()
    {
        var time = new ManualTime();
        await using var shell = new HardwareShell(new Backends(), Everything, time);

        Assert.Equal(ExecuteResult.Accepted, shell.Execute(Wakeup(300)));
        time.Advance(299);
        Assert.False(shell.TryTake(out _));
        time.Advance(1);

        Assert.True(shell.TryTake(out Event? fired));
        Assert.IsType<Event.WakeupFired>(fired);
        Assert.False(shell.TryTake(out _));
    }

    [Fact]
    public async Task ANewScheduleReplacesAPendingWakeup()
    {
        var time = new ManualTime();
        await using var shell = new HardwareShell(new Backends(), Everything, time);
        shell.Execute(Wakeup(300));
        time.Advance(200);

        shell.Execute(Wakeup(300));
        time.Advance(200);
        Assert.False(shell.TryTake(out _));
        time.Advance(100);

        Assert.True(shell.TryTake(out Event? fired));
        Assert.IsType<Event.WakeupFired>(fired);
        Assert.False(shell.TryTake(out _));
    }

    [Fact]
    public async Task AWaitingHostIsWokenByAWakeupScheduledWhileItWaits()
    {
        var time = new ManualTime();
        await using var shell = new HardwareShell(new Backends(), Everything, time);
        shell.Execute(Wakeup(50));
        Task<Event> waiting = shell.Next();
        Assert.False(waiting.IsCompleted);

        time.Advance(50);

        Assert.IsType<Event.WakeupFired>(await waiting.WaitAsync(Wait.Limit));
    }

    [Fact]
    public async Task AWaitEndsWithNothingWhenTheHostCancelsIt()
    {
        await using var shell = new HardwareShell(AllBackends(), Everything);
        using var cancelled = new CancellationTokenSource();
        Task<Event?> waiting = shell.NextAsync(cancelled.Token);

        cancelled.Cancel();

        Assert.Null(await waiting.WaitAsync(Wait.Limit));
    }

    [Fact]
    public async Task DisposingStopsEveryRunningPortAndRefusesLaterCommands()
    {
        var discovery = new FakeDiscovery();
        var central = new FakeCentral();
        var reader = new FakeReader();
        var shell = new HardwareShell(
            new Backends { Discovery = discovery, Central = central, Nfc = reader },
            Everything);
        shell.Execute(new Command.BleStartScanning { ServiceUuid = "s" });
        shell.Execute(new Command.BleConnect { DeviceId = "p" });
        shell.Execute(new Command.NfcActivate { Payload = Array.Empty<byte>() });
        Call<string> scan = await discovery.NextCall();
        Call<Dial> dial = await central.NextCall();
        await reader.Waiting;

        await shell.DisposeAsync();

        await scan.Stopped;
        await dial.Stopped;
        await reader.Abandoned;
        Assert.Equal(ExecuteResult.Closed, shell.Execute(new Command.BleStartScanning { ServiceUuid = "s" }));
        Assert.Equal(ExecuteResult.Closed, shell.Execute(Wakeup(1)));
        Assert.False(shell.TryTake(out _));
    }

    [Fact]
    public async Task DisposingWaitsForABackendThatIgnoresItsStopOnlyUntilTheGraceHasPassed()
    {
        var time = new ManualTime();
        var deaf = new DeafDiscovery();
        var shell = new HardwareShell(new Backends { Discovery = deaf }, Everything, time);
        shell.Execute(new Command.BleStartScanning { ServiceUuid = "s" });
        await deaf.Started;

        Task disposing = shell.DisposeAsync().AsTask();
        time.Advance((int)HardwareShell.ShutdownGrace.TotalMilliseconds - 1);
        // Completed here would mean the shell did not wait for its backend at all.
        Assert.False(disposing.IsCompleted);
        time.Advance(1);

        await disposing.WaitAsync(Wait.Limit);
        Assert.Equal(ExecuteResult.Closed, shell.Execute(new Command.BleStartScanning { ServiceUuid = "s" }));
    }

    [Fact]
    public async Task ABusyPortDoesNotStarveAnother()
    {
        var discovery = new FakeDiscovery();
        var central = new FakeCentral();
        await using var shell = new HardwareShell(new Backends { Discovery = discovery, Central = central }, Everything);
        shell.Execute(new Command.BleStartScanning { ServiceUuid = "s" });
        shell.Execute(new Command.BleConnect { DeviceId = "p" });
        Call<string> scan = await discovery.NextCall();
        Call<Dial> dial = await central.NextCall();
        for (int index = 0; index < 10; index++)
        {
            await scan.Send(new Event.HardwareUnavailable { Transport = "scan" });
        }
        await dial.Send(new Event.BleConnected { DeviceId = "p", Direction = BleLinkDirection.Outbound });

        var firstTwo = new List<string> { (await shell.Next()).Wire(), (await shell.Next()).Wire() };

        Assert.Contains("{\"BleConnected\":{\"device_id\":\"p\",\"direction\":\"Outbound\"}}", firstTwo);
    }

    [Fact]
    public async Task TheSharedFixturesHardwareCommandsRunAndItsHardwareEventsPassThroughUnchanged()
    {
        using JsonDocument fixture = JsonDocument.Parse(ContractFixtures.ProtocolConformance());
        var central = new FakeCentral();
        await using var shell = new HardwareShell(new Backends { Central = central }, Everything);
        Command[] commands = fixture.RootElement.GetProperty("commands").EnumerateArray()
            .Select(entry => ContractJson.DecodeCommand(Encoding.UTF8.GetBytes(entry.GetRawText())))
            .ToArray();
        string[] hardwareEvents = fixture.RootElement.GetProperty("events").EnumerateArray()
            .Select(entry => entry.GetRawText())
            .Where(text => text.Contains("\"Ble", StringComparison.Ordinal) || text.Contains("\"Hardware", StringComparison.Ordinal))
            .ToArray();
        var write = Assert.IsType<Command.BleWriteCharacteristic>(commands[0]);
        var disconnect = Assert.IsType<Command.BleDisconnect>(commands[1]);

        shell.Execute(new Command.BleConnect { DeviceId = write.DeviceId });
        Call<Dial> dial = await central.NextCall();
        Assert.Equal(ExecuteResult.Accepted, shell.Execute(write));
        var request = Assert.IsType<LinkRequest.Write>(await dial.Arguments.Requests.ReadAsync().AsTask().WaitAsync(Wait.Limit));
        Assert.Equal(write.Uuid, request.Uuid);
        Assert.Equal(new byte[] { 1, 2, 3 }, request.Data);
        Assert.Equal(2, hardwareEvents.Length);
        foreach (string text in hardwareEvents)
        {
            Event observation = ContractJson.DecodeEvent(Encoding.UTF8.GetBytes(text));
            await dial.Send(observation);
            Assert.Same(observation, await shell.Next());
        }
        Assert.Equal(ExecuteResult.Accepted, shell.Execute(disconnect));
        await dial.Stopped;
    }
}
