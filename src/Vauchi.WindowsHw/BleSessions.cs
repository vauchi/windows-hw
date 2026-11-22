// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Threading.Channels;
using Vauchi.Contract;

namespace Vauchi.WindowsHw;

/// <summary>Bounds on what Core hands a Bluetooth port, checked before a backend sees it.</summary>
internal static class BleBounds
{
    /// <summary>A Windows device id is a 48-bit address in decimal; this leaves room without taking arbitrary input.</summary>
    internal const int MaxDeviceId = 128;

    internal const int MaxUuid = 128;

    /// <summary>The longest attribute value ATT carries.</summary>
    internal const int MaxValue = 512;

    internal const int MaxQueued = 64;

    internal static bool Fits(string deviceId, string uuid, byte[]? data) =>
        deviceId.Length <= MaxDeviceId && uuid.Length <= MaxUuid && (data?.Length ?? 0) <= MaxValue;
}

internal sealed class ScannerSession(PortSession session, IDiscovery discovery)
{
    internal PortSession Session { get; } = session;

    internal DeliveryError? Start(string serviceUuid) =>
        serviceUuid.Length > BleBounds.MaxUuid
            ? DeliveryError.PayloadTooLarge
            : Session.Start((events, stop) => discovery.ScanAsync(serviceUuid, events, stop));
}

/// <summary>The peripheral role: one advertisement at a time and the notifications Core sends on it.</summary>
internal sealed class AdvertiserSession(PortSession session, IAdvertising advertising)
{
    private ChannelWriter<Notification>? _outbound;

    internal PortSession Session { get; } = session;

    internal DeliveryError? Start(string serviceUuid, byte[] payload)
    {
        if (!BleBounds.Fits("", serviceUuid, payload))
        {
            return DeliveryError.PayloadTooLarge;
        }
        Channel<Notification> outbound = Channel.CreateBounded<Notification>(BleBounds.MaxQueued);
        DeliveryError? refusal = Session.Start(
            (events, stop) => advertising.AdvertiseAsync(serviceUuid, payload, outbound.Reader, events, stop));
        _outbound = refusal is null ? outbound.Writer : null;
        return refusal;
    }

    internal DeliveryError? Notify(Notification notification)
    {
        if (!BleBounds.Fits(notification.DeviceId, notification.Uuid, notification.Data))
        {
            return DeliveryError.PayloadTooLarge;
        }
        if (_outbound is null)
        {
            return DeliveryError.Inactive;
        }
        return _outbound.TryWrite(notification) ? null : DeliveryError.Full;
    }

    internal void Stop()
    {
        _outbound = null;
        Session.Stop();
    }
}

/// <summary>The central role: one outbound link, kept until Core disconnects it or dials another peer.</summary>
internal sealed class CentralSession(PortSession session, ICentral central)
{
    private (string DeviceId, ChannelWriter<LinkRequest> Requests)? _link;

    internal PortSession Session { get; } = session;

    internal DeliveryError? Connect(string deviceId)
    {
        if (!BleBounds.Fits(deviceId, "", null))
        {
            return DeliveryError.PayloadTooLarge;
        }
        Channel<LinkRequest> requests = Channel.CreateBounded<LinkRequest>(BleBounds.MaxQueued);
        DeliveryError? refusal = Session.Start(
            (events, stop) => central.ConnectAsync(deviceId, requests.Reader, events, stop));
        _link = refusal is null ? (deviceId, requests.Writer) : null;
        return refusal;
    }

    internal DeliveryError? Request(string deviceId, LinkRequest request)
    {
        (string uuid, byte[]? data) = request switch
        {
            LinkRequest.Write write => (write.Uuid, write.Data),
            LinkRequest.Read read => (read.Uuid, null),
            _ => ("", null),
        };
        if (!BleBounds.Fits(deviceId, uuid, data))
        {
            return DeliveryError.PayloadTooLarge;
        }
        if (_link is not { } link || link.DeviceId != deviceId)
        {
            return DeliveryError.Inactive;
        }
        return link.Requests.TryWrite(request) ? null : DeliveryError.Full;
    }

    internal DeliveryError? Disconnect(string deviceId)
    {
        if (_link is not { } link || link.DeviceId != deviceId)
        {
            return DeliveryError.Inactive;
        }
        _link = null;
        Session.Stop();
        return null;
    }
}
