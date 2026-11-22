// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vauchi.Contract;

namespace Vauchi.WindowsHw;

public enum ExecuteResult
{
    /// <summary>The command reached its port; what happens is reported as events.</summary>
    Accepted,

    /// <summary>
    /// The shell has no port for the command. It reports nothing: the
    /// capability report told Core what it runs (owner, 2026-11-02,
    /// private#770).
    /// </summary>
    Unsupported,

    /// <summary>A queue is full: take events, then send the command again.</summary>
    Full,

    /// <summary>The command addresses a link or a tag that is not there.</summary>
    Inactive,

    /// <summary>A value in the command is larger than its port accepts.</summary>
    TooLarge,

    /// <summary>The shell was disposed.</summary>
    Closed,
}

/// <summary>
/// The entry point an app hands Core's hardware commands to (ADR-077 item
/// 4, ADR-078 item 3). Each command goes to the session that owns its
/// port; the shell decides nothing beyond that and shows no UI. A host may
/// send commands on one thread and take observations on another; backends
/// report from any thread.
/// </summary>
public sealed class HardwareShell : IAsyncDisposable
{
    /// <summary>
    /// How long disposal waits for the ports to return after stopping
    /// them. A backend that ignores its stop is left behind after that: a
    /// closing app or an exiting helper must not hang on it.
    /// </summary>
    public static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);

    private readonly object _host = new();
    private readonly Signal _available = new();
    private readonly TimeProvider _time;
    private readonly DeviceCapabilities _capabilities;
    private readonly ScannerSession? _scanner;
    private readonly AdvertiserSession? _advertiser;
    private readonly CentralSession? _central;
    private readonly NfcSession? _nfc;
    private readonly PortSession? _camera;
    private readonly ICamera? _cameraPort;
    private readonly DirectSession? _direct;
    private readonly List<PortSession> _sources = new();
    private string? _preview;
    private long? _wakeupDue;
    private int _nextSource;
    private bool _closed;

    /// <param name="backends">The ports this host has.</param>
    /// <param name="device">What the host's probe found.</param>
    /// <param name="time">The clock wakeups run on; the system's when null.</param>
    public HardwareShell(Backends backends, DeviceCapabilities device, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(backends);
        ArgumentNullException.ThrowIfNull(device);
        _time = time ?? TimeProvider.System;
        bool ble = backends is { Discovery: not null, Advertising: not null, Central: not null };
        // Never more than the backends can run (ADR-077 item 7).
        _capabilities = device with
        {
            HasBle = device.HasBle && ble,
            HasNfc = device.HasNfc && backends.Nfc is not null,
            HasCamera = device.HasCamera && backends.Camera is not null,
        };
        if (backends.Discovery is { } discovery)
        {
            _scanner = new ScannerSession(Source(Limits.Discovery, "BLE", "Bluetooth discovery failed"), discovery);
        }
        if (backends.Advertising is { } advertising)
        {
            _advertiser = new AdvertiserSession(Source(Limits.Gatt, "BLE", "Bluetooth advertising failed"), advertising);
        }
        if (backends.Central is { } central)
        {
            _central = new CentralSession(Source(Limits.Gatt, "BLE", "Bluetooth link failed"), central);
        }
        if (backends.Nfc is { } nfc)
        {
            _nfc = new NfcSession(Source(Limits.Nfc, "NFC", "NFC reader failed"), nfc);
        }
        if (backends.Camera is { } camera)
        {
            _camera = Source(Limits.Camera, "Camera", "camera capture failed");
            _cameraPort = camera;
        }
        if (backends.Direct is { } direct)
        {
            _direct = new DirectSession(Source(Limits.Direct, DirectSession.Transport, DirectSession.Failure), direct);
        }
    }

    /// <summary>The capability report the host dispatches to Core once, before any command.</summary>
    public Event Report() => new Event.HardwareCapabilitiesReported { Capabilities = _capabilities };

    public ExecuteResult Execute(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_host)
        {
            return _closed ? ExecuteResult.Closed : Route(command);
        }
    }

    private ExecuteResult Route(Command command) =>
        command switch
        {
            Command.BleStartScanning scan when _scanner is not null => Result(_scanner.Start(scan.ServiceUuid)),
            Command.BleStopScanning when _scanner is not null => Stopped(_scanner.Session),
            Command.BleStartAdvertising advertise when _advertiser is not null =>
                Result(_advertiser.Start(advertise.ServiceUuid, advertise.Payload)),
            Command.BleStopAdvertising when _advertiser is not null => Done(_advertiser.Stop),
            Command.BleWriteCharacteristic { Direction: BleLinkDirection.Inbound } write when _advertiser is not null =>
                Result(_advertiser.Notify(new Notification(write.DeviceId, write.Uuid, write.Data))),
            Command.BleConnect connect when _central is not null => Result(_central.Connect(connect.DeviceId)),
            Command.BleWriteCharacteristic { Direction: BleLinkDirection.Outbound } write when _central is not null =>
                Result(_central.Request(write.DeviceId, new LinkRequest.Write(write.Uuid, write.Data))),
            Command.BleReadCharacteristic { Direction: BleLinkDirection.Outbound } read when _central is not null =>
                Result(_central.Request(read.DeviceId, new LinkRequest.Read(read.Uuid))),
            Command.BleDisconnect { Direction: BleLinkDirection.Outbound } disconnect when _central is not null =>
                Result(_central.Disconnect(disconnect.DeviceId)),
            Command.NfcActivate activate when _nfc is not null => Result(_nfc.Activate(activate.Apdus)),
            Command.NfcSendApdu send when _nfc is not null => Result(_nfc.Send(send.Apdus)),
            Command.NfcDeactivate when _nfc is not null => Done(_nfc.Deactivate),
            Command.CameraCapture capture when _camera is not null => Capture(capture),
            Command.CameraStop stop when _camera is not null => StopCapture(stop.PreviewId),
            Command.DirectSend send when _direct is not null =>
                Result(_direct.Send(send.Payload, send.IsInitiator, send.Endpoint)),
            Command.ScheduleWakeup wakeup => Schedule(wakeup.DelayMillis),
            _ => ExecuteResult.Unsupported,
        };

    /// <summary>
    /// The next observation from any port. Each call starts one port
    /// further on, so a busy port cannot starve the others.
    /// </summary>
    public bool TryTake(out Event? observation)
    {
        lock (_host)
        {
            return Take(out observation);
        }
    }

    private bool Take(out Event? observation)
    {
        if (_wakeupDue is { } due && _time.GetTimestamp() >= due)
        {
            _wakeupDue = null;
            observation = new Event.WakeupFired();
            return true;
        }
        for (int offset = 0; offset < _sources.Count; offset++)
        {
            PortSession source = _sources[(_nextSource + offset) % _sources.Count];
            if (source.Pop() is { } taken)
            {
                _nextSource = (_nextSource + offset + 1) % _sources.Count;
                observation = taken;
                return true;
            }
        }
        observation = null;
        return false;
    }

    /// <summary>
    /// Waits for the next observation; null when <paramref name="stop"/> is
    /// cancelled first. A host with its own loop calls
    /// <see cref="TryTake"/> instead.
    /// </summary>
    public async Task<Event?> NextAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            // Taken before looking: an observation that arrives between the
            // look and the wait has already completed this task.
            Task arrival = _available.NextAsync();
            if (TryTake(out Event? observation))
            {
                return observation;
            }
            try
            {
                await WaitAsync(arrival, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        Task returned;
        lock (_host)
        {
            _closed = true;
            _wakeupDue = null;
            returned = Task.WhenAll(_sources.ConvertAll(source => source.Shutdown()));
        }
        using var waited = new CancellationTokenSource();
        Task grace = Task.Delay(ShutdownGrace, _time, waited.Token);
        await Task.WhenAny(returned, grace).ConfigureAwait(false);
        waited.Cancel();
    }

    private static ExecuteResult Result(DeliveryError? refusal) => refusal switch
    {
        null => ExecuteResult.Accepted,
        DeliveryError.Full => ExecuteResult.Full,
        DeliveryError.Inactive => ExecuteResult.Inactive,
        DeliveryError.Closed => ExecuteResult.Closed,
        _ => ExecuteResult.TooLarge,
    };

    private static ExecuteResult Stopped(PortSession session) => Done(session.Stop);

    private static ExecuteResult Done(Action act)
    {
        act();
        return ExecuteResult.Accepted;
    }

    private PortSession Source(InboxLimits limits, string transport, string failure)
    {
        var session = new PortSession(limits, _available, transport, failure);
        _sources.Add(session);
        return session;
    }

    private ExecuteResult Capture(Command.CameraCapture capture)
    {
        if (capture.PreviewId.Length > BleBounds.MaxDeviceId)
        {
            return ExecuteResult.TooLarge;
        }
        DeliveryError? refusal = _camera!.Start(
            (events, stop) => _cameraPort!.CaptureAsync(capture.PreviewId, capture.Purpose, events, stop));
        _preview = refusal is null ? capture.PreviewId : null;
        return Result(refusal);
    }

    private ExecuteResult StopCapture(string previewId)
    {
        // A stop for a preview that is no longer captured must not end its successor.
        if (_preview != previewId)
        {
            return ExecuteResult.Inactive;
        }
        _preview = null;
        return Stopped(_camera!);
    }

    private ExecuteResult Schedule(uint delayMillis)
    {
        // A new schedule replaces a pending one, so a sub-second heartbeat never fires twice.
        _wakeupDue = _time.GetTimestamp() + (long)(delayMillis * (_time.TimestampFrequency / 1000.0));
        _available.Set();
        return ExecuteResult.Accepted;
    }

    private async Task WaitAsync(Task arrival, CancellationToken stop)
    {
        long? pending;
        lock (_host)
        {
            pending = _wakeupDue;
        }
        if (pending is not { } due)
        {
            await arrival.WaitAsync(stop).ConfigureAwait(false);
            return;
        }
        TimeSpan left = _time.GetElapsedTime(_time.GetTimestamp(), due);
        if (left <= TimeSpan.Zero)
        {
            return;
        }
        using var either = CancellationTokenSource.CreateLinkedTokenSource(stop);
        Task wakeup = Task.Delay(left, _time, either.Token);
        await Task.WhenAny(arrival, wakeup).ConfigureAwait(false);
        either.Cancel();
        stop.ThrowIfCancellationRequested();
    }

    /// <summary>One operation per port; the event and byte budgets are linux-hw's.</summary>
    private static class Limits
    {
        /// <summary>2048 bytes holds the largest observation Core accepts (private#770).</summary>
        internal static readonly InboxLimits Discovery = new(1, 64, 2048);

        /// <summary>A 512-byte attribute value, ATT's longest, is about 2.1 KB as JSON.</summary>
        internal static readonly InboxLimits Gatt = new(1, 64, 4096);

        internal static readonly InboxLimits Nfc = new(1, 16, 4096);

        /// <summary>
        /// One exchange at a time. Its answer is at most the 64 KiB frame
        /// bound, and a byte is up to four characters of a JSON array.
        /// </summary>
        internal static readonly InboxLimits Direct = new(1, 4, 1 << 19);

        internal static readonly InboxLimits Camera = new(1, 16, 1 << 16);
    }
}
