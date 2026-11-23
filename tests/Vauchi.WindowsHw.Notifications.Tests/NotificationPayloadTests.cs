// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Linq;
using System.Xml.Linq;
using Vauchi.Contract;
using Vauchi.WindowsHw.Notifications;
using Xunit;

namespace Vauchi.WindowsHw.Notifications.Tests;

/// <summary>
/// What a notification shows: Core's prepared title and body, as text and
/// only as text (vauchi/private#772; windows-app posted the same two lines).
/// </summary>
public class NotificationPayloadTests
{
    private static NotificationSpec Spec(string title, string body) => new()
    {
        NotificationId = "opaque-notification-17",
        Title = title,
        Body = body,
        DeepLinkUri = null,
        CategoryId = "category",
        ChannelId = "channel",
        Urgency = NotificationUrgency.Default,
        CategoryOptions = Array.Empty<string>(),
    };

    [Fact]
    public void TheTitleAndTheBodyAreTheTwoLinesShown()
    {
        XElement toast = XElement.Parse(NotificationPayload.Xml(Spec("Prepared title", "Prepared body")));

        Assert.Equal("toast", toast.Name.LocalName);
        XElement binding = toast.Element("visual")!.Element("binding")!;
        Assert.Equal("ToastGeneric", (string?)binding.Attribute("template"));
        Assert.Equal(new[] { "Prepared title", "Prepared body" }, binding.Elements("text").Select(text => text.Value));
    }

    [Theory]
    [InlineData("<text>injected</text>", "</binding><binding template=\"ToastGeneric\">")]
    [InlineData("Tom & Jerry \"quoted\" 'single'", "a < b > c")]
    [InlineData("Ärger ☃ \U0001F600", "")]
    public void CoresTextIsShownAsTextAndNeverAsMarkup(string title, string body)
    {
        // DC-01: the text came from a contact's card through Core; markup in
        // it must not add elements or attributes to the notification.
        XElement toast = XElement.Parse(NotificationPayload.Xml(Spec(title, body)));

        Assert.Single(toast.Descendants("binding"));
        Assert.Equal(new[] { title, body }, toast.Descendants("text").Select(text => text.Value));
    }

    [Theory]
    [InlineData("bell \u0007", "nul \u0000 end")]
    public void ACharacterXmlCannotCarryIsLeftOut(string title, string body)
    {
        XElement toast = XElement.Parse(NotificationPayload.Xml(Spec(title, body)));

        Assert.Equal(new[] { "bell ", "nul  end" }, toast.Descendants("text").Select(text => text.Value));
    }
}
