// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vauchi.Contract;

namespace Vauchi.WindowsHw;

/// <summary>One grey (luma) camera frame, one byte per pixel, row after row.</summary>
public sealed record LumaFrame(int Width, int Height, byte[] Pixels);

/// <summary>An open camera stream. Disposal releases the camera.</summary>
public interface ILumaFrames : IAsyncDisposable
{
    /// <summary>The next frame, or null when the camera ended the stream.</summary>
    ValueTask<LumaFrame?> NextAsync(CancellationToken stop);
}

/// <summary>
/// The OS call that opens a camera. It throws
/// <see cref="HardwareFailureException"/> when the OS says why it cannot.
/// </summary>
public interface ICameraSource
{
    /// <param name="previewId">
    /// Core's id for this capture: the display shell shows the preview in the
    /// surface it hosts under the same id (ADR-077 amendment 2026-11-01).
    /// </param>
    Task<ILumaFrames> OpenAsync(string previewId, CancellationToken stop);
}

/// <summary>Every QR code a decoder finds in one frame, in the order found.</summary>
public interface IQrDecoder
{
    IReadOnlyList<string> Decode(LumaFrame frame);
}

/// <summary>
/// The camera port over a frame source and a QR decoder, as
/// <c>linux-hw</c>'s camera executor (<c>src/camera.rs</c>): frames until
/// one carries a code, each distinct code of that frame once as
/// <c>QrScanned</c>, then the camera is released. Facing a phone, a camera
/// can see the peer's code and a reflection of its own, so every code is
/// reported (vauchi/private#450); Core tells them apart.
/// </summary>
public sealed class QrCamera(ICameraSource source, IQrDecoder decoder) : ICamera
{
    /// <summary>The most characters a QR code holds (version 40, alphanumeric).</summary>
    public const int MaxCodeLength = 4296;

    /// <summary>A frame wider or taller than this is not a camera's (DC-01).</summary>
    public const int MaxSide = 8192;

    public async Task CaptureAsync(string previewId, CameraPurpose purpose, EventSink events, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (purpose != CameraPurpose.QrScan)
        {
            await events.SendAsync(
                new Event.HardwareError { Transport = "Camera", Error = $"camera purpose {purpose} has no port on Windows" },
                stop).ConfigureAwait(false);
            return;
        }
        ILumaFrames frames = await source.OpenAsync(previewId, stop).ConfigureAwait(false);
        await using (frames.ConfigureAwait(false))
        {
            while (await frames.NextAsync(stop).ConfigureAwait(false) is { } frame)
            {
                if (!Whole(frame))
                {
                    continue;
                }
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var found = new List<string>();
                foreach (string code in decoder.Decode(frame))
                {
                    if (code is { Length: > 0 and <= MaxCodeLength } && seen.Add(code))
                    {
                        found.Add(code);
                    }
                }
                if (found.Count == 0)
                {
                    continue;
                }
                foreach (string code in found)
                {
                    if (await events.SendAsync(new Event.QrScanned { Data = code }, stop).ConfigureAwait(false)
                        == SendOutcome.Ended)
                    {
                        return;
                    }
                }
                return;
            }
        }
    }

    private static bool Whole(LumaFrame frame) =>
        frame is { Width: > 0 and <= MaxSide, Height: > 0 and <= MaxSide, Pixels: not null }
        && frame.Pixels.Length == (long)frame.Width * frame.Height;
}
