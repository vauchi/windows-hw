// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using Vauchi.WindowsHw.Native;
using Xunit;

namespace Vauchi.WindowsHw.Native.Tests;

/// <summary>
/// What the probe reports of the cameras Windows lists. Core offers a
/// camera switch only when the count says there is more than one
/// (vauchi/private#772): a laptop with one camera has nothing to switch to.
/// </summary>
public class WindowsProbeTests
{
    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(1, true, 1)]
    [InlineData(2, true, 2)]
    [InlineData(300, true, 255)]
    public void TheCamerasWindowsListsAreCounted(int listed, bool hasCamera, int counted)
    {
        (bool HasCamera, byte? CameraCount) report = WindowsProbe.Cameras(listed);

        Assert.Equal(hasCamera, report.HasCamera);
        Assert.Equal((byte)counted, report.CameraCount);
    }

    [Fact]
    public void AQuestionThatFailedClaimsNoCameraAndNoCount()
    {
        (bool HasCamera, byte? CameraCount) report = WindowsProbe.Cameras(null);

        Assert.False(report.HasCamera);
        Assert.Null(report.CameraCount);
    }
}
