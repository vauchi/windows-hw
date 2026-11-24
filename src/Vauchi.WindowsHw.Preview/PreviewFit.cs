// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System;

namespace Vauchi.WindowsHw.Preview;

/// <summary>
/// How a frame sits in the surface: scaled uniformly to fit whole, and
/// centred. <see cref="None"/> when either has no size.
/// </summary>
public readonly record struct PreviewFit(double Scale, double Left, double Top)
{
    public static PreviewFit None => default;

    public static PreviewFit Into(int frameWidth, int frameHeight, double surfaceWidth, double surfaceHeight)
    {
        if (frameWidth <= 0 || frameHeight <= 0
            || !double.IsFinite(surfaceWidth) || !double.IsFinite(surfaceHeight)
            || surfaceWidth <= 0 || surfaceHeight <= 0)
        {
            return None;
        }
        double scale = Math.Min(surfaceWidth / frameWidth, surfaceHeight / frameHeight);
        return new PreviewFit(
            scale,
            (surfaceWidth - frameWidth * scale) / 2,
            (surfaceHeight - frameHeight * scale) / 2);
    }
}
