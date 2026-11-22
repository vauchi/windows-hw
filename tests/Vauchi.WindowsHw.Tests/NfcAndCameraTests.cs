// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Linq;
using System.Threading.Tasks;
using Vauchi.Contract;
using Xunit;

namespace Vauchi.WindowsHw.Tests;

public class NfcAndCameraTests
{
    private static readonly byte[] Select = { 0x00, 0xA4, 0x04, 0x00 };
    private static readonly byte[] Exchange = { 0x80, 0x10, 0x00, 0x00 };
    private static readonly byte[] Ok = { 0x90, 0x00 };

    private static Command Activate(params byte[][] apdus) =>
        new Command.NfcActivate { Payload = Array.Empty<byte>(), Apdus = apdus };

    private static Command Send(params byte[][] apdus) =>
        new Command.NfcSendApdu { Data = Array.Empty<byte>(), Apdus = apdus };

    private static string Answer(params byte[] bytes) => new Event.NfcApduReceived { Bytes = bytes }.Wire();

    private static string Failed(string reason) => new Event.NfcFailed { Reason = reason }.Wire();

    [Fact]
    public async Task ActivationWaitsForATagThenTransmitsCoresFramesUnchangedAndReportsEachAnswer()
    {
        var reader = new FakeReader();
        var card = new FakeCard().Answers(Ok, new byte[] { 1, 2, 0x90, 0x00 });
        await using var shell = new HardwareShell(new Backends { Nfc = reader }, new DeviceCapabilities());

        Assert.Equal(ExecuteResult.Accepted, shell.Execute(Activate(Select, Exchange)));
        await reader.Waiting;
        Assert.False(shell.TryTake(out _));
        reader.TagArrives(card);

        Assert.Equal(Answer(Ok), (await shell.Next()).Wire());
        Assert.Equal(Answer(1, 2, 0x90, 0x00), (await shell.Next()).Wire());
        Assert.Equal(new[] { Select, Exchange }, card.Transmitted);
    }

    [Fact]
    public async Task LaterFramesAreSentOnTheTagTheActivationConnected()
    {
        var reader = new FakeReader();
        var card = new FakeCard().Answers(Ok, new byte[] { 7, 0x90, 0x00 });
        await using var shell = new HardwareShell(new Backends { Nfc = reader }, new DeviceCapabilities());
        shell.Execute(Activate(Select));
        reader.TagArrives(card);
        await shell.Next();

        Assert.Equal(ExecuteResult.Accepted, shell.Execute(Send(Exchange)));

        Assert.Equal(Answer(7, 0x90, 0x00), (await shell.Next()).Wire());
        Assert.Equal(new[] { Select, Exchange }, card.Transmitted);
    }

    [Fact]
    public async Task FramesWithNoActivationReportThatNoTagIsConnected()
    {
        await using var shell = new HardwareShell(new Backends { Nfc = new FakeReader() }, new DeviceCapabilities());

        Assert.Equal(ExecuteResult.Accepted, shell.Execute(Send(Exchange)));

        Assert.Equal(Failed("no NFC tag is connected"), (await shell.Next()).Wire());
    }

    [Fact]
    public async Task ATagThatFailsIsReportedWithTheReadersReasonAndIsGoneForLaterFrames()
    {
        var reader = new FakeReader();
        var card = new FakeCard().Answers(Ok).ThenFails(new NfcException("SCARD_W_REMOVED_CARD"));
        await using var shell = new HardwareShell(new Backends { Nfc = reader }, new DeviceCapabilities());
        shell.Execute(Activate(Select, Exchange, Exchange));
        reader.TagArrives(card);

        Assert.Equal(Answer(Ok), (await shell.Next()).Wire());
        Assert.Equal(Failed("SCARD_W_REMOVED_CARD"), (await shell.Next()).Wire());
        await card.Released;
        Assert.Equal(2, card.Transmitted.Count);

        Assert.Equal(ExecuteResult.Accepted, shell.Execute(Send(Exchange)));
        Assert.Equal(Failed("no NFC tag is connected"), (await shell.Next()).Wire());
        Assert.Equal(2, card.Transmitted.Count);
    }

    [Fact]
    public async Task AFailureThatIsNotTheReadersOwnIsReportedWithoutItsText()
    {
        var reader = new FakeReader();
        var card = new FakeCard().ThenFails(new InvalidOperationException("handle 0x1234 of reader ACR122"));
        await using var shell = new HardwareShell(new Backends { Nfc = reader }, new DeviceCapabilities());
        shell.Execute(Activate(Select));
        reader.TagArrives(card);

        Assert.Equal(Failed("NFC reader failed"), (await shell.Next()).Wire());
    }

    [Fact]
    public async Task AReaderThatCannotConnectReportsItsReason()
    {
        var reader = new FakeReader();
        await using var shell = new HardwareShell(new Backends { Nfc = reader }, new DeviceCapabilities());
        shell.Execute(Activate(Select));

        reader.Fails(new NfcException("SCARD_E_NO_READERS_AVAILABLE"));

        Assert.Equal(Failed("SCARD_E_NO_READERS_AVAILABLE"), (await shell.Next()).Wire());
    }

    [Fact]
    public async Task DeactivationEndsTheWaitAndReportsNothing()
    {
        var reader = new FakeReader();
        await using var shell = new HardwareShell(new Backends { Nfc = reader }, new DeviceCapabilities());
        shell.Execute(Activate(Select));
        await reader.Waiting;

        Assert.Equal(ExecuteResult.Accepted, shell.Execute(new Command.NfcDeactivate()));

        await reader.Abandoned;
        Assert.False(shell.TryTake(out _));
    }

    [Fact]
    public async Task DeactivationLetsGoOfAConnectedTag()
    {
        var reader = new FakeReader();
        var card = new FakeCard().Answers(Ok);
        await using var shell = new HardwareShell(new Backends { Nfc = reader }, new DeviceCapabilities());
        shell.Execute(Activate(Select));
        reader.TagArrives(card);
        await shell.Next();

        shell.Execute(new Command.NfcDeactivate());

        await card.Released;
    }

    [Theory]
    [InlineData(64, 16, ExecuteResult.Accepted)]
    [InlineData(65, 16, ExecuteResult.TooLarge)]
    [InlineData(1, 65_535, ExecuteResult.Accepted)]
    [InlineData(1, 65_536, ExecuteResult.TooLarge)]
    public async Task MoreFramesOrLongerFramesThanTheBoundsAreRefused(int frames, int length, ExecuteResult expected)
    {
        await using var shell = new HardwareShell(new Backends { Nfc = new FakeReader() }, new DeviceCapabilities());
        byte[][] apdus = Enumerable.Range(0, frames).Select(_ => new byte[length]).ToArray();

        Assert.Equal(expected, shell.Execute(Activate(apdus)));
    }

    [Fact]
    public async Task ACaptureReachesTheCameraPortWithItsPreviewAndWhatItDecodesReachesTheHost()
    {
        var camera = new FakeCamera();
        await using var shell = new HardwareShell(new Backends { Camera = camera }, new DeviceCapabilities());
        Event scanned = new Event.QrScanned { Data = "opaque" };

        Assert.Equal(
            ExecuteResult.Accepted,
            shell.Execute(new Command.CameraCapture { PreviewId = "preview-1", Purpose = CameraPurpose.QrScan }));
        Call<Capture> capture = await camera.NextCall();
        await capture.Send(scanned);

        Assert.Equal(new Capture("preview-1", CameraPurpose.QrScan), capture.Arguments);
        Assert.Equal(scanned.Wire(), (await shell.Next()).Wire());
    }

    [Fact]
    public async Task AStopForAnotherPreviewLeavesTheCaptureRunning()
    {
        var camera = new FakeCamera();
        await using var shell = new HardwareShell(new Backends { Camera = camera }, new DeviceCapabilities());
        shell.Execute(new Command.CameraCapture { PreviewId = "preview-2", Purpose = CameraPurpose.QrScan });
        Call<Capture> capture = await camera.NextCall();

        Assert.Equal(ExecuteResult.Inactive, shell.Execute(new Command.CameraStop { PreviewId = "preview-1" }));
        Assert.False(capture.Stop.IsCancellationRequested);

        Assert.Equal(ExecuteResult.Accepted, shell.Execute(new Command.CameraStop { PreviewId = "preview-2" }));
        await capture.Stopped;
        Assert.Equal(ExecuteResult.Inactive, shell.Execute(new Command.CameraStop { PreviewId = "preview-2" }));
    }
}
