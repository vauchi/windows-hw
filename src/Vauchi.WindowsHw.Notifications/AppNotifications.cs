// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Windows.AppNotifications;
using Vauchi.Contract;

namespace Vauchi.WindowsHw.Notifications;

/// <summary>
/// The notification port over the Windows App SDK. The app that hands it to
/// its hardware shell has the App SDK running; a Windows that cannot show
/// app notifications is reported unavailable.
/// </summary>
public sealed class AppNotifications : INotifications
{
    public Task PostAsync(NotificationSpec notification, EventSink events, CancellationToken stop)
    {
        if (!AppNotificationManager.IsSupported())
        {
            throw new HardwareFailureException(HardwareFailure.Unavailable);
        }
        AppNotificationManager.Default.Show(new AppNotification(NotificationPayload.Xml(notification)));
        return Task.CompletedTask;
    }
}
