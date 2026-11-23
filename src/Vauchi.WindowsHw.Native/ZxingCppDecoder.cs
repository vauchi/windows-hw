// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Vauchi.WindowsHw.Native;

/// <summary>
/// The camera port's QR decoder, zxing-cpp. Chosen on frames of a phone's
/// Glance code from a laptop webcam: it read 15 of 240 full-resolution
/// frames where ZXing.Net read none (vauchi/private#772, 2026-11-23).
/// </summary>
public sealed class ZxingCppDecoder : IQrDecoder
{
    /// <summary>
    /// The folder, beside the application, that holds zxing-cpp's native
    /// library. Its file is named ZXing.dll, as ZXing.Net's assembly is
    /// named zxing.dll, and the app draws its codes with ZXing.Net: in one
    /// folder of a case-insensitive file system one would replace the other.
    /// </summary>
    public const string NativeFolder = "zxing-cpp";

    static ZxingCppDecoder()
    {
        NativeLibrary.SetDllImportResolver(typeof(ZXingCpp.BarcodeReader).Assembly, ResolveNative);
    }

    private readonly ZXingCpp.BarcodeReader _reader = new() { Formats = ZXingCpp.BarcodeFormat.QRCode };

    public IReadOnlyList<string> Decode(LumaFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        // The native reader trusts the size it is given (DC-01).
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Pixels is null
            || frame.Pixels.Length != (long)frame.Width * frame.Height)
        {
            throw new ArgumentException(
                $"a {frame.Width}x{frame.Height} frame needs {(long)frame.Width * frame.Height} pixels, not {frame.Pixels?.Length}",
                nameof(frame));
        }
        var view = new ZXingCpp.ImageView(frame.Pixels, frame.Width, frame.Height, ZXingCpp.ImageFormat.Lum);
        return _reader.From(view).Select(code => code.Text).ToArray();
    }

    private static IntPtr ResolveNative(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) =>
        string.Equals(Path.GetFileNameWithoutExtension(libraryName), "ZXing", StringComparison.OrdinalIgnoreCase)
            ? NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, NativeFolder, "ZXing.dll"))
            : IntPtr.Zero;
}
