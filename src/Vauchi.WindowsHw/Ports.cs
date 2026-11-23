// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Vauchi.Contract;

namespace Vauchi.WindowsHw;

/// <summary>Why a port could not do what it was asked, as far as the OS says.</summary>
public enum HardwareFailure
{
    /// <summary>The device or its radio is not there or is switched off.</summary>
    Unavailable,

    /// <summary>The OS refused access; Core has the display shell ask.</summary>
    PermissionDenied,

    /// <summary>The OS call failed.</summary>
    Failed,
}

/// <summary>
/// Thrown by a port to end its operation with a failure the session
/// reports. Any other exception from a port is reported as
/// <see cref="HardwareFailure.Failed"/>.
/// </summary>
public sealed class HardwareFailureException : Exception
{
    public HardwareFailureException(HardwareFailure failure)
        : base(failure.ToString()) => Failure = failure;

    public HardwareFailure Failure { get; }
}

/// <summary>
/// A value Core writes on an inbound link (<c>BleWriteCharacteristic</c>
/// with <c>Inbound</c>), sent to that central as a notification.
/// </summary>
public sealed record Notification(string DeviceId, string Uuid, byte[] Data);

/// <summary>What Core asks of the outbound link while it is up.</summary>
public abstract record LinkRequest
{
    private LinkRequest()
    {
    }

    public sealed record Write(string Uuid, byte[] Data) : LinkRequest;

    public sealed record Read(string Uuid) : LinkRequest;
}

/// <summary>
/// OS discovery port. An implementation bounds what it captures before it
/// builds an event, reports <c>BleAdvertisementObserved</c>, and releases
/// native discovery when <paramref name="stop"/> is cancelled.
/// </summary>
public interface IDiscovery
{
    Task ScanAsync(string serviceUuid, EventSink events, CancellationToken stop);
}

/// <summary>
/// OS peripheral port: advertise, serve the exchange's GATT service and
/// report what centrals do with it, every link <c>Inbound</c>. Cancelling
/// <paramref name="stop"/> withdraws the advertisement and the service.
/// </summary>
public interface IAdvertising
{
    Task AdvertiseAsync(
        string serviceUuid,
        byte[] payload,
        ChannelReader<Notification> outbound,
        EventSink events,
        CancellationToken stop);
}

/// <summary>
/// OS central port: dial the peer's GATT server and carry Core's requests
/// on that link. It reports <c>BleConnected</c>, what the peer sends, read
/// answers and <c>BleDisconnected</c>, all <c>Outbound</c>. Cancelling
/// <paramref name="stop"/> disconnects.
/// </summary>
public interface ICentral
{
    Task ConnectAsync(
        string deviceId,
        ChannelReader<LinkRequest> requests,
        EventSink events,
        CancellationToken stop);
}

/// <summary>
/// A reader or tag failure with the OS's description, which the session
/// passes to Core unchanged as <c>NfcFailed</c>.
/// </summary>
public sealed class NfcException : Exception
{
    public NfcException(string reason)
        : base(reason)
    {
    }
}

/// <summary>
/// A connected tag: transmits one APDU and returns the raw answer, status
/// word included. Nothing here reads a status word.
/// </summary>
public interface INfcCard : IDisposable
{
    byte[] Transmit(byte[] apdu);
}

/// <summary>The NFC reader port: completes when a tag connects.</summary>
public interface INfcReader
{
    Task<INfcCard> WaitForTagAsync(CancellationToken stop);
}

/// <summary>
/// The camera port: capture for one preview id, decode, and report
/// <c>QrScanned</c> and <c>QrScanProgress</c> until cancelled. The preview
/// surface reaches the backend through the app's wiring, not through here
/// (ADR-077 amendment 2026-11-01).
/// </summary>
public interface ICamera
{
    Task CaptureAsync(string previewId, CameraPurpose purpose, EventSink events, CancellationToken stop);
}

/// <summary>
/// The direct-send port: one framed exchange with the peer at Core's
/// endpoint, blocking until the peer's payload arrives, Core's deadlines
/// pass or <paramref name="stop"/> is cancelled. Core chose the endpoint,
/// the role and the deadlines.
/// </summary>
public interface IDirectExchange
{
    byte[] Exchange(DirectEndpoint endpoint, byte[] payload, bool isInitiator, CancellationToken stop);
}

/// <summary>
/// A direct exchange that did not complete. <see cref="Exception.Message"/>
/// is what Core receives: it names the step, never an address or a byte.
/// </summary>
public sealed class DirectExchangeException : Exception
{
    public DirectExchangeException(string step)
        : base(step)
    {
    }
}

/// <summary>
/// The OS backends the shell runs on. A missing one leaves its commands
/// refused and its capability unclaimed.
/// </summary>
/// <summary>
/// The notification port: shows one notification Core prepared in every
/// word. It throws <see cref="HardwareFailureException"/> when the OS says
/// why it cannot.
/// </summary>
public interface INotifications
{
    Task PostAsync(NotificationSpec notification, EventSink events, CancellationToken stop);
}

public sealed record Backends
{
    public INotifications? Notifications { get; init; }

    public IDiscovery? Discovery { get; init; }

    public IAdvertising? Advertising { get; init; }

    public ICentral? Central { get; init; }

    public INfcReader? Nfc { get; init; }

    public ICamera? Camera { get; init; }

    public IDirectExchange? Direct { get; init; }
}
