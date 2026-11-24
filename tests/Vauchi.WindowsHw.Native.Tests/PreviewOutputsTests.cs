// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using Vauchi.WindowsHw.Native;
using Windows.Graphics.Imaging;
using Xunit;

namespace Vauchi.WindowsHw.Native.Tests;

/// <summary>
/// Where the camera's frames are shown: the surface the display shell hosts
/// under Core's preview id, which the app hands over (ADR-077 amendment
/// 2026-11-01, vauchi/private#772).
/// </summary>
public class PreviewOutputsTests
{
    private sealed class Output : IPreviewOutput
    {
        public int Shown { get; private set; }

        public void Show(SoftwareBitmap frame) => Shown++;
    }

    [Fact]
    public void AnAttachedOutputIsFoundUnderItsPreviewId()
    {
        var outputs = new PreviewOutputs();
        var output = new Output();

        outputs.Attach("camera-1", output);

        Assert.True(outputs.TryGet("camera-1", out IPreviewOutput? found));
        Assert.Same(output, found);
        Assert.False(outputs.TryGet("camera-2", out _));
    }

    [Fact]
    public void ADetachedOutputIsGone()
    {
        var outputs = new PreviewOutputs();
        var output = new Output();
        outputs.Attach("camera-1", output);

        outputs.Detach("camera-1", output);

        Assert.False(outputs.TryGet("camera-1", out _));
    }

    [Fact]
    public void ALateDetachOfAReplacedOutputKeepsTheNewOne()
    {
        // A viewport shown again under the same id replaces its surface; the
        // old surface's detach, arriving after, must not remove the new one.
        var outputs = new PreviewOutputs();
        var old = new Output();
        var replacement = new Output();
        outputs.Attach("camera-1", old);
        outputs.Attach("camera-1", replacement);

        outputs.Detach("camera-1", old);

        Assert.True(outputs.TryGet("camera-1", out IPreviewOutput? found));
        Assert.Same(replacement, found);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void AnEmptyPreviewIdIsRefused(string? previewId)
    {
        Assert.ThrowsAny<ArgumentException>(() => new PreviewOutputs().Attach(previewId!, new Output()));
    }
}
