// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

// qr-bench <dir-of-pgm-frames>: each decoder on every frame, at full and
// half resolution. A code's text is never printed: a Vauchi QR carries key
// material (logging rules); its length and a hash prefix tell whether the
// decoders read the same code.
if (args.Length < 1)
{
    Console.WriteLine("usage: qr-bench <dir-of-pgm-frames>");
    return 2;
}

#if !ZXING_CPP
if (args.Length > 1 && args[0] == "known")
{
    // known <dir>: one 1920x1080 grey frame with a code of known text, to
    // check the bench reads a frame the way the camera saves it.
    var matrix = new ZXing.QrCode.QRCodeWriter().encode("vauchi bench check 0123456789", ZXing.BarcodeFormat.QR_CODE, 0, 0);
    const int Width = 1920, Height = 1080, Scale = 6, Left = 700, Top = 300;
    var pixels = Enumerable.Repeat((byte)120, Width * Height).ToArray();
    for (int y = 0; y < matrix.Height * Scale; y++)
    {
        for (int x = 0; x < matrix.Width * Scale; x++)
        {
            pixels[(Top + y) * Width + Left + x] = matrix[x / Scale, y / Scale] ? (byte)20 : (byte)235;
        }
    }
    Directory.CreateDirectory(args[1]);
    File.WriteAllBytes(Path.Combine(args[1], "frame-0000.pgm"), Encoding.ASCII.GetBytes($"P5\n{Width} {Height}\n255\n").Concat(pixels).ToArray());
    Console.WriteLine($"written: {matrix.Width} modules, length 29");
    return 0;
}
#endif

var frames = Directory.GetFiles(args[0], "*.pgm").OrderBy(path => path, StringComparer.Ordinal).Select(Frame.Load).ToList();
Console.WriteLine($"{frames.Count} frame(s), {frames.FirstOrDefault()?.Width}x{frames.FirstOrDefault()?.Height}");

var decoders = new (string Name, Func<Frame, string[]> Decode)[]
{
#if ZXING_CPP
    ("zxing-cpp", frame => ZxingCpp(frame, tryHarder: false)),
    ("zxing-cpp harder", frame => ZxingCpp(frame, tryHarder: true)),
#else
    ("zxing.net", frame => ZxingNet(frame, tryHarder: false)),
    ("zxing.net harder", frame => ZxingNet(frame, tryHarder: true)),
#endif
};

foreach (bool half in new[] { false, true })
{
    var input = half ? frames.Select(frame => frame.Half()).ToList() : frames;
    Console.WriteLine();
    Console.WriteLine(half ? $"half resolution ({input[0].Width}x{input[0].Height})" : "full resolution");
    Console.WriteLine($"{"decoder",-18} {"frames read",11} {"codes",6} {"mean ms",8} {"p95 ms",7}  codes seen (length:hash)");
    foreach ((string name, Func<Frame, string[]> decode) in decoders)
    {
        decode(input[0]);
        var times = new List<double>();
        var seen = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int read = 0, codes = 0;
        foreach (Frame frame in input)
        {
            var clock = Stopwatch.StartNew();
            string[] found = decode(frame);
            times.Add(clock.Elapsed.TotalMilliseconds);
            read += found.Length > 0 ? 1 : 0;
            codes += found.Length;
            foreach (string text in found)
            {
                string key = $"{text.Length}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8]}";
                seen[key] = seen.GetValueOrDefault(key) + 1;
            }
        }
        times.Sort();
        double p95 = times[(int)Math.Min(times.Count - 1, Math.Ceiling(times.Count * 0.95) - 1)];
        Console.WriteLine($"{name,-18} {read,5}/{input.Count,-5} {codes,6} {times.Average(),8:F1} {p95,7:F1}  {string.Join(" ", seen.Select(entry => $"{entry.Key}x{entry.Value}"))}");
    }
}
return 0;

#if !ZXING_CPP
static string[] ZxingNet(Frame frame, bool tryHarder)
{
    var source = new ZXing.RGBLuminanceSource(frame.Pixels, frame.Width, frame.Height, ZXing.RGBLuminanceSource.BitmapFormat.Gray8);
    var reader = new ZXing.QrCode.QRCodeReader();
    var hints = new Dictionary<ZXing.DecodeHintType, object>();
    if (tryHarder)
    {
        hints[ZXing.DecodeHintType.TRY_HARDER] = true;
    }
    var multi = new ZXing.Multi.QrCode.QRCodeMultiReader();
    var bitmap = new ZXing.BinaryBitmap(new ZXing.Common.HybridBinarizer(source));
    ZXing.Result[]? results = multi.decodeMultiple(bitmap, hints);
    if (results is null || results.Length == 0)
    {
        ZXing.Result? single = reader.decode(bitmap, hints);
        return single is null ? Array.Empty<string>() : new[] { single.Text };
    }
    return results.Select(result => result.Text).Distinct(StringComparer.Ordinal).ToArray();
}

#endif

#if ZXING_CPP
static string[] ZxingCpp(Frame frame, bool tryHarder)
{
    var view = new ZXingCpp.ImageView(frame.Pixels, frame.Width, frame.Height, ZXingCpp.ImageFormat.Lum);
    var reader = new ZXingCpp.BarcodeReader
    {
        Formats = ZXingCpp.BarcodeFormat.QRCode,
        TryHarder = tryHarder,
    };
    return reader.From(view).Select(code => code.Text).Distinct(StringComparer.Ordinal).ToArray();
}
#endif

internal sealed record Frame(int Width, int Height, byte[] Pixels)
{
    public static Frame Load(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        int offset = 0;
        string Token()
        {
            while (char.IsWhiteSpace((char)data[offset]))
            {
                offset++;
            }
            int start = offset;
            while (!char.IsWhiteSpace((char)data[offset]))
            {
                offset++;
            }
            return Encoding.ASCII.GetString(data, start, offset - start);
        }
        if (Token() != "P5")
        {
            throw new InvalidDataException($"{path}: not a binary PGM");
        }
        int width = int.Parse(Token());
        int height = int.Parse(Token());
        Token();
        offset++;
        return new Frame(width, height, data[offset..(offset + width * height)]);
    }

    public Frame Half()
    {
        int width = Width / 2, height = Height / 2;
        var pixels = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = 2 * y * Width + 2 * x;
                pixels[y * width + x] = (byte)((Pixels[at] + Pixels[at + 1] + Pixels[at + Width] + Pixels[at + Width + 1]) / 4);
            }
        }
        return new Frame(width, height, pixels);
    }
}
