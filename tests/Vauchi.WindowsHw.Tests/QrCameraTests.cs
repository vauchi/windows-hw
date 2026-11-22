// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Vauchi.Contract;
using Xunit;

namespace Vauchi.WindowsHw.Tests;

/// <summary>
/// The camera port over a frame source and a QR decoder, against scripted
/// ones: CI has no camera. What it reports is <c>linux-hw</c>'s
/// (<c>src/camera.rs</c>): frames until one carries a code, each code of
/// that frame once as <c>QrScanned</c>, then the capture is over.
/// </summary>
public class QrCameraTests
{
    /// <summary>A camera whose frames the test hands in, one at a time.</summary>
    private sealed class FakeSource : ICameraSource, ILumaFrames
    {
        private readonly Channel<LumaFrame?> _frames = Channel.CreateUnbounded<LumaFrame?>();
        private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HardwareFailure? OpenFailure { get; init; }

        public Task Opened => _opened.Task.WaitAsync(Wait.Limit);

        public Task Closed => _closed.Task.WaitAsync(Wait.Limit);

        public void Show(LumaFrame frame) => _frames.Writer.TryWrite(frame);

        public void End() => _frames.Writer.TryWrite(null);

        public Task<ILumaFrames> OpenAsync(CancellationToken stop)
        {
            if (OpenFailure is { } failure)
            {
                throw new HardwareFailureException(failure);
            }
            _opened.TrySetResult();
            return Task.FromResult<ILumaFrames>(this);
        }

        public async ValueTask<LumaFrame?> NextAsync(CancellationToken stop) => await _frames.Reader.ReadAsync(stop);

        public ValueTask DisposeAsync()
        {
            _closed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Reads the codes a frame's first pixel names, and counts what it was given.</summary>
    private sealed class FakeDecoder : IQrDecoder
    {
        private readonly Dictionary<byte, string[]> _codes = new();

        public int Decoded { get; private set; }

        public FakeDecoder Reads(byte marker, params string[] codes)
        {
            _codes[marker] = codes;
            return this;
        }

        public IReadOnlyList<string> Decode(LumaFrame frame)
        {
            Decoded++;
            return _codes.GetValueOrDefault(frame.Pixels[0], Array.Empty<string>());
        }
    }

    private static LumaFrame Frame(byte marker, int width = 4, int height = 4)
    {
        var pixels = new byte[width * height];
        if (pixels.Length > 0)
        {
            pixels[0] = marker;
        }
        return new LumaFrame(width, height, pixels);
    }

    private static HardwareShell ShellOver(FakeSource source, FakeDecoder decoder) =>
        new(new Backends { Camera = new QrCamera(source, decoder) }, new DeviceCapabilities());

    private static Command Scan(string preview = "camera-1") =>
        new Command.CameraCapture { PreviewId = preview, Purpose = CameraPurpose.QrScan };

    private static string Scanned(string data) => new Event.QrScanned { Data = data }.Wire();

    [Fact]
    public async Task FramesAreReadUntilOneCarriesACodeWhichIsReportedOnceAndTheCameraIsReleased()
    {
        var source = new FakeSource();
        var decoder = new FakeDecoder().Reads(7, "vauchi:peer");
        await using HardwareShell shell = ShellOver(source, decoder);

        Assert.Equal(ExecuteResult.Accepted, shell.Execute(Scan()));
        await source.Opened;
        source.Show(Frame(0));
        source.Show(Frame(0));
        source.Show(Frame(7));
        source.Show(Frame(7));

        Assert.Equal(Scanned("vauchi:peer"), (await shell.Next()).Wire());
        await source.Closed;
        Assert.Equal(3, decoder.Decoded);
    }

    [Fact]
    public async Task EveryDistinctCodeOfThatFrameIsReportedInTheOrderDecoded()
    {
        // linux-hw keeps every code: facing a phone, a camera can see the
        // peer's code and a reflection of its own (vauchi/private#450).
        var source = new FakeSource();
        var decoder = new FakeDecoder().Reads(3, "reflection", "peer", "reflection");
        await using HardwareShell shell = ShellOver(source, decoder);
        shell.Execute(Scan());
        await source.Opened;

        source.Show(Frame(3));

        Assert.Equal(Scanned("reflection"), (await shell.Next()).Wire());
        Assert.Equal(Scanned("peer"), (await shell.Next()).Wire());
        await source.Closed;
        Assert.False(shell.TryTake(out _));
    }

    [Theory]
    [InlineData(0, 4, 0)]
    [InlineData(4, 0, 0)]
    [InlineData(-1, 4, 4)]
    [InlineData(4, 4, 15)]
    [InlineData(4, 4, 17)]
    [InlineData(8193, 1, 8193)]
    public async Task AFrameWhoseSizeDoesNotAddUpIsNotDecoded(int width, int height, int length)
    {
        var source = new FakeSource();
        var decoder = new FakeDecoder().Reads(8, "malformed").Reads(9, "code");
        await using HardwareShell shell = ShellOver(source, decoder);
        shell.Execute(Scan());
        await source.Opened;
        var pixels = new byte[length];
        if (length > 0)
        {
            pixels[0] = 8;
        }

        source.Show(new LumaFrame(width, height, pixels));
        source.Show(Frame(9));

        Assert.Equal(Scanned("code"), (await shell.Next()).Wire());
        Assert.Equal(1, decoder.Decoded);
    }

    [Fact]
    public async Task ACodeLongerThanAQrCodeHoldsIsNotReported()
    {
        var source = new FakeSource();
        var decoder = new FakeDecoder()
            .Reads(1, new string('a', QrCamera.MaxCodeLength + 1))
            .Reads(2, new string('b', QrCamera.MaxCodeLength));
        await using HardwareShell shell = ShellOver(source, decoder);
        shell.Execute(Scan());
        await source.Opened;

        source.Show(Frame(1));
        source.Show(Frame(2));

        var scanned = Assert.IsType<Event.QrScanned>(await shell.Next());
        Assert.Equal(QrCamera.MaxCodeLength, scanned.Data.Length);
    }

    [Fact]
    public async Task AnEmptyCodeIsNotACode()
    {
        var source = new FakeSource();
        var decoder = new FakeDecoder().Reads(4, "").Reads(6, "", "after");
        await using HardwareShell shell = ShellOver(source, decoder);
        shell.Execute(Scan());
        await source.Opened;

        source.Show(Frame(4));
        source.Show(Frame(6));

        Assert.Equal(Scanned("after"), (await shell.Next()).Wire());
        await source.Closed;
        Assert.False(shell.TryTake(out _));
    }

    [Fact]
    public async Task ACameraThatEndsItsStreamEndsTheCaptureWithNothingToReport()
    {
        var source = new FakeSource();
        await using HardwareShell shell = ShellOver(source, new FakeDecoder());
        shell.Execute(Scan());
        await source.Opened;

        source.End();
        await source.Closed;

        Assert.Equal(ExecuteResult.Accepted, shell.Execute(Scan("camera-2")));
        Assert.False(shell.TryTake(out _));
    }

    [Theory]
    [InlineData(HardwareFailure.Unavailable)]
    [InlineData(HardwareFailure.PermissionDenied)]
    public async Task WhatTheOsSaysAboutTheCameraIsWhatCoreHears(HardwareFailure failure)
    {
        var source = new FakeSource { OpenFailure = failure };
        await using HardwareShell shell = ShellOver(source, new FakeDecoder());
        Event expected = failure == HardwareFailure.Unavailable
            ? new Event.HardwareUnavailable { Transport = "Camera" }
            : new Event.PermissionDenied { Transport = "Camera" };

        shell.Execute(Scan());

        Assert.Equal(expected.Wire(), (await shell.Next()).Wire());
    }

    [Fact]
    public async Task APhotoHasNoPortOnWindowsYet()
    {
        var source = new FakeSource();
        await using HardwareShell shell = ShellOver(source, new FakeDecoder());

        shell.Execute(new Command.CameraCapture { PreviewId = "camera-1", Purpose = CameraPurpose.Photo });

        Assert.Equal(
            new Event.HardwareError { Transport = "Camera", Error = "camera purpose Photo has no port on Windows" }.Wire(),
            (await shell.Next()).Wire());
        Assert.False(source.Opened.IsCompleted);
    }

    [Fact]
    public async Task StoppingReleasesTheCameraAndReportsNothing()
    {
        var source = new FakeSource();
        var decoder = new FakeDecoder().Reads(5, "late");
        await using HardwareShell shell = ShellOver(source, decoder);
        shell.Execute(Scan());
        await source.Opened;

        Assert.Equal(ExecuteResult.Accepted, shell.Execute(new Command.CameraStop { PreviewId = "camera-1" }));
        await source.Closed;
        source.Show(Frame(5));

        Assert.Equal(ExecuteResult.Accepted, shell.Execute(Scan("camera-2")));
        Assert.False(shell.TryTake(out _));
    }

    [Fact]
    public async Task WithACameraTheShellClaimsOne()
    {
        await using HardwareShell shell = ShellOver(new FakeSource(), new FakeDecoder());
        await using var without = new HardwareShell(new Backends(), new DeviceCapabilities { HasCamera = true });
        await using var with = new HardwareShell(
            new Backends { Camera = new QrCamera(new FakeSource(), new FakeDecoder()) },
            new DeviceCapabilities { HasCamera = true });

        Assert.False(Assert.IsType<Event.HardwareCapabilitiesReported>(without.Report()).Capabilities.HasCamera);
        Assert.True(Assert.IsType<Event.HardwareCapabilitiesReported>(with.Report()).Capabilities.HasCamera);
    }
}
