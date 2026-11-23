// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using System.Xml;
using System.Xml.Linq;
using Vauchi.Contract;

namespace Vauchi.WindowsHw.Notifications;

/// <summary>
/// The notification Windows shows for Core's prepared one: its title and
/// body as two lines of text. The XML is written by an XML writer, so text
/// a contact chose never becomes markup (DC-01).
/// </summary>
public static class NotificationPayload
{
    public static string Xml(NotificationSpec notification) =>
        new XElement(
            "toast",
            new XElement(
                "visual",
                new XElement(
                    "binding",
                    new XAttribute("template", "ToastGeneric"),
                    new XElement("text", Carried(notification.Title)),
                    new XElement("text", Carried(notification.Body)))))
        .ToString(SaveOptions.DisableFormatting);

    // XML 1.0 has no way to write most control characters, escaped or not.
    // A character outside the Basic Multilingual Plane is two UTF-16 units,
    // kept as a pair.
    private static string Carried(string text)
    {
        var kept = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (i + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[i + 1], text[i]))
            {
                kept.Append(text, i, 2);
                i++;
            }
            else if (XmlConvert.IsXmlChar(text[i]))
            {
                kept.Append(text[i]);
            }
        }
        return kept.ToString();
    }
}
