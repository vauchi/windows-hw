// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Vauchi.WindowsHw;
using Vauchi.WindowsHw.Native;
using Xunit;

namespace Vauchi.WindowsHw.Native.Tests;

/// <summary>
/// The camera port's decoder (vauchi/private#772). zxing-cpp was chosen on
/// frames of a phone's Glance code from AC's webcam: it read 15 of 240
/// full-resolution frames where ZXing.Net read none (2026-11-23).
/// </summary>
public class ZxingCppDecoderTests
{
    private const int Width = 640;
    private const int Height = 480;

    [Fact]
    public void AFrameCarryingACodeNamesWhatTheCodeHolds()
    {
        LumaFrame frame = Frame(("vauchi decoder check 0123456789", 120, 80));

        Assert.Equal(new[] { "vauchi decoder check 0123456789" }, new ZxingCppDecoder().Decode(frame));
    }

    [Fact]
    public void EveryCodeInAFrameIsReported()
    {
        // A camera facing a phone can see the peer's code and a reflection of
        // its own (vauchi/private#450): both reach Core, which tells them apart.
        LumaFrame frame = Frame(("left code", 20, 120), ("right code", 340, 120));

        string[] codes = new ZxingCppDecoder().Decode(frame).ToArray();

        Array.Sort(codes, StringComparer.Ordinal);
        Assert.Equal(new[] { "left code", "right code" }, codes);
    }

    [Fact]
    public void AFrameWithoutACodeHasNone()
    {
        var pixels = new byte[Width * Height];
        new Random(7).NextBytes(pixels);

        Assert.Empty(new ZxingCppDecoder().Decode(new LumaFrame(Width, Height, pixels)));
    }

    [Theory]
    [InlineData(0, 10, 0)]
    [InlineData(10, 10, 99)]
    [InlineData(10, 10, 101)]
    [InlineData(-1, 10, 10)]
    public void AFrameWhosePixelsDoNotMatchItsSizeIsRefused(int width, int height, int pixelCount)
    {
        // DC-01: the bytes come from a driver, and the native reader trusts
        // the size it is given.
        var frame = new LumaFrame(width, height, new byte[pixelCount]);

        Assert.Throws<ArgumentException>(() => new ZxingCppDecoder().Decode(frame));
    }

    [Fact]
    public void ZxingCppsNativeLibraryAndZxingNetLiveSideBySide()
    {
        // zxing-cpp's native library is named ZXing.dll and ZXing.Net's
        // assembly zxing.dll: one folder holds only one of them on a
        // case-insensitive file system, so the native one has its own.
        string root = AppContext.BaseDirectory;

        Assert.True(File.Exists(Path.Combine(root, ZxingCppDecoder.NativeFolder, "ZXing.dll")),
            $"zxing-cpp's native library is not in {ZxingCppDecoder.NativeFolder}/");
        Assert.Equal("zxing", AssemblyName.GetAssemblyName(Path.Combine(root, "zxing.dll")).Name);
        Assert.Equal(new[] { "side by side" }, new ZxingCppDecoder().Decode(Frame(("side by side", 120, 80))));
    }

    private static LumaFrame Frame(params (string Text, int Left, int Top)[] codes)
    {
        var pixels = new byte[Width * Height];
        Array.Fill(pixels, (byte)255);
        foreach ((string text, int left, int top) in codes)
        {
            var matrix = new ZXing.QrCode.QRCodeWriter().encode(text, ZXing.BarcodeFormat.QR_CODE, 280, 280);
            for (int y = 0; y < matrix.Height; y++)
            {
                for (int x = 0; x < matrix.Width; x++)
                {
                    if (matrix[x, y])
                    {
                        pixels[(top + y) * Width + left + x] = 0;
                    }
                }
            }
        }
        return new LumaFrame(Width, Height, pixels);
    }
}
