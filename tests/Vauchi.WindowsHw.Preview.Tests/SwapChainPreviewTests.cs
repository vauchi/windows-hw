// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using Vauchi.WindowsHw.Preview;
using Windows.Graphics.Imaging;
using Xunit;

namespace Vauchi.WindowsHw.Preview.Tests;

/// <summary>
/// The camera preview: the hardware shell draws each frame into a swap chain
/// it hands to the display shell's neutral surface (ADR-077 amendment
/// 2026-11-01, vauchi/private#772).
/// </summary>
public class SwapChainPreviewTests
{
    private sealed class Host : ISwapChainHost
    {
        public List<IntPtr> Attached { get; } = new();

        public (double Width, double Height) Size { get; set; } = (400, 200);

        public void Attach(IntPtr swapChain) => Attached.Add(swapChain);
    }

    private static SoftwareBitmap Frame(int width, int height, byte blue, byte green, byte red)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = blue;
            pixels[i + 1] = green;
            pixels[i + 2] = red;
            pixels[i + 3] = 255;
        }
        return SoftwareBitmap.CreateCopyFromBuffer(
            pixels.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
    }

    [Fact]
    public void AFrameIsDrawnIntoTheSwapChainTheHostHolds()
    {
        var host = new Host();
        using var preview = new SwapChainPreview(host, PreviewDevice.Software);

        using SoftwareBitmap red = Frame(4, 2, 0, 0, 255);
        preview.Show(red);

        IntPtr swapChain = Assert.Single(host.Attached);
        Assert.NotEqual(IntPtr.Zero, swapChain);
        (int width, int height, byte[] pixels) = preview.ReadBack();
        Assert.Equal((4, 2), (width, height));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels[..4]);
    }

    [Fact]
    public void EachFrameReplacesTheLastOnTheSameSwapChain()
    {
        var host = new Host();
        using var preview = new SwapChainPreview(host, PreviewDevice.Software);

        using SoftwareBitmap red = Frame(4, 2, 0, 0, 255);
        using SoftwareBitmap blue = Frame(4, 2, 255, 0, 0);
        preview.Show(red);
        preview.Show(blue);

        Assert.Single(host.Attached);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, preview.ReadBack().Pixels[..4]);
    }

    [Fact]
    public void AFrameOfAnotherSizeResizesTheSwapChain()
    {
        var host = new Host();
        using var preview = new SwapChainPreview(host, PreviewDevice.Software);

        using SoftwareBitmap small = Frame(4, 2, 0, 0, 255);
        using SoftwareBitmap large = Frame(8, 6, 0, 255, 0);
        preview.Show(small);
        preview.Show(large);

        (int width, int height, byte[] pixels) = preview.ReadBack();
        Assert.Equal((8, 6), (width, height));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, pixels[..4]);
    }

    [Theory]
    [InlineData(4, 2, 400, 200, 100, 0, 0)]
    [InlineData(4, 2, 400, 400, 100, 0, 100)]
    [InlineData(4, 2, 200, 400, 50, 0, 150)]
    [InlineData(640, 480, 320, 480, 0.5, 0, 120)]
    public void TheFrameIsFittedIntoTheSurfaceWholeAndCentred(
        int frameWidth, int frameHeight, double surfaceWidth, double surfaceHeight,
        double scale, double left, double top)
    {
        PreviewFit fit = PreviewFit.Into(frameWidth, frameHeight, surfaceWidth, surfaceHeight);

        Assert.Equal(scale, fit.Scale, 6);
        Assert.Equal(left, fit.Left, 6);
        Assert.Equal(top, fit.Top, 6);
    }

    [Theory]
    [InlineData(0, 2, 400, 200)]
    [InlineData(4, 2, 0, 200)]
    [InlineData(4, 2, double.NaN, 200)]
    public void NothingIsFittedIntoNothing(int frameWidth, int frameHeight, double surfaceWidth, double surfaceHeight)
    {
        Assert.Equal(PreviewFit.None, PreviewFit.Into(frameWidth, frameHeight, surfaceWidth, surfaceHeight));
    }

    [Fact]
    public void AFrameThatIsNotBgraIsRefused()
    {
        var host = new Host();
        using var preview = new SwapChainPreview(host, PreviewDevice.Software);
        using var grey = new SoftwareBitmap(BitmapPixelFormat.Gray8, 4, 2);

        Assert.Throws<ArgumentException>(() => preview.Show(grey));
        Assert.Empty(host.Attached);
    }
}
