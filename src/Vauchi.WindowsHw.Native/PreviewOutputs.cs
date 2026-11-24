// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Windows.Graphics.Imaging;

namespace Vauchi.WindowsHw.Native;

/// <summary>
/// Where camera frames are shown: something that draws them on the surface
/// the display shell hosts for one preview id. Called on the camera's
/// thread with a BGRA8 frame it must not keep.
/// </summary>
public interface IPreviewOutput
{
    void Show(SoftwareBitmap frame);
}

/// <summary>
/// The preview outputs by Core's preview id. The app attaches the output
/// for a viewport the display shell shows and detaches it when the viewport
/// goes (ADR-077 amendment 2026-11-01); the camera source looks up the one
/// for the capture it runs. Safe across threads.
/// </summary>
public sealed class PreviewOutputs
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IPreviewOutput> _outputs = new(StringComparer.Ordinal);

    public void Attach(string previewId, IPreviewOutput output)
    {
        ArgumentException.ThrowIfNullOrEmpty(previewId);
        ArgumentNullException.ThrowIfNull(output);
        lock (_gate)
        {
            _outputs[previewId] = output;
        }
    }

    /// <summary>Removes <paramref name="output"/>, and only it: a newer output under the id stays.</summary>
    public void Detach(string previewId, IPreviewOutput output)
    {
        lock (_gate)
        {
            if (_outputs.TryGetValue(previewId, out IPreviewOutput? current) && ReferenceEquals(current, output))
            {
                _outputs.Remove(previewId);
            }
        }
    }

    public bool TryGet(string previewId, [NotNullWhen(true)] out IPreviewOutput? output)
    {
        lock (_gate)
        {
            return _outputs.TryGetValue(previewId, out output);
        }
    }
}
