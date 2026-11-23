// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Threading.Tasks;
using Vauchi.Contract;
using Xunit;

namespace Vauchi.WindowsHw.Tests;

/// <summary>
/// PostNotification is a hardware-target command (Core's command targets):
/// the shell hands Core's prepared notification to the host's notification
/// port, which windows-app executed itself until vauchi/private#772.
/// </summary>
public class NotificationTests
{
    private static readonly NotificationSpec Prepared = new()
    {
        NotificationId = "opaque-notification-17",
        Title = "Prepared title",
        Body = "Prepared body",
        DeepLinkUri = null,
        CategoryId = "category",
        ChannelId = "channel",
        Urgency = NotificationUrgency.Default,
        CategoryOptions = Array.Empty<string>(),
    };

    [Fact]
    public async Task ANotificationReachesThePortAsCorePreparedIt()
    {
        var notifications = new FakeNotifications();
        await using var shell = new HardwareShell(new Backends { Notifications = notifications }, new DeviceCapabilities());

        Assert.Equal(ExecuteResult.Accepted, shell.Execute(new Command.PostNotification { Notification = Prepared }));

        Call<NotificationSpec> post = await notifications.NextCall();
        Assert.Same(Prepared, post.Arguments);
    }

    [Fact]
    public async Task WithoutANotificationPortTheShellRunsNone()
    {
        await using var shell = new HardwareShell(new Backends(), new DeviceCapabilities());

        Assert.Equal(ExecuteResult.Unsupported, shell.Execute(new Command.PostNotification { Notification = Prepared }));
    }

    [Fact]
    public async Task AFailedPostIsReportedInFixedWords()
    {
        var notifications = new FakeNotifications();
        await using var shell = new HardwareShell(new Backends { Notifications = notifications }, new DeviceCapabilities());
        shell.Execute(new Command.PostNotification { Notification = Prepared });
        Call<NotificationSpec> post = await notifications.NextCall();

        post.Fail(new InvalidOperationException("the OS's own words, which may name the user"));

        var error = Assert.IsType<Event.HardwareError>(await shell.Next());
        Assert.Equal("Notification", error.Transport);
        Assert.Equal("notification failed", error.Error);
    }

    [Fact]
    public async Task AHostWithoutNotificationsSaysSo()
    {
        var notifications = new FakeNotifications();
        await using var shell = new HardwareShell(new Backends { Notifications = notifications }, new DeviceCapabilities());
        shell.Execute(new Command.PostNotification { Notification = Prepared });
        Call<NotificationSpec> post = await notifications.NextCall();

        post.Fail(new HardwareFailureException(HardwareFailure.Unavailable));

        Assert.Equal("Notification", Assert.IsType<Event.HardwareUnavailable>(await shell.Next()).Transport);
    }
}
