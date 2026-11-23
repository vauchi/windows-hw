// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Vauchi.Contract;

namespace Vauchi.WindowsHw.Tests;

/// <summary>
/// One call of a port, as the fake saw it: its arguments, the sink it may
/// report through, and whether the shell has stopped it.
/// </summary>
public sealed class Call<TArguments>
{
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _end = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Call(TArguments arguments, EventSink events, CancellationToken stop)
    {
        Arguments = arguments;
        Events = events;
        Stop = stop;
        stop.Register(() => _stopped.TrySetResult());
    }

    public TArguments Arguments { get; }

    public EventSink Events { get; }

    public CancellationToken Stop { get; }

    /// <summary>Completes when the shell cancels this call.</summary>
    public Task Stopped => _stopped.Task.WaitAsync(Wait.Limit);

    internal Task End => _end.Task;

    public ValueTask<SendOutcome> Send(Event observation) => Events.SendAsync(observation, Stop);

    /// <summary>The backend's work ends by itself.</summary>
    public void Complete() => _end.TrySetResult();

    /// <summary>The backend's work ends with this exception.</summary>
    public void Fail(Exception failure) => _end.TrySetException(failure);
}

/// <summary>A port whose every call is handed to the test.</summary>
public abstract class ScriptedPort<TArguments>
{
    private readonly Channel<Call<TArguments>> _calls = Channel.CreateUnbounded<Call<TArguments>>();

    /// <summary>The next call the shell made, in order.</summary>
    public async Task<Call<TArguments>> NextCall() =>
        await _calls.Reader.ReadAsync().AsTask().WaitAsync(Wait.Limit);

    public bool HasCall => _calls.Reader.TryPeek(out _);

    protected async Task Run(TArguments arguments, EventSink events, CancellationToken stop)
    {
        var call = new Call<TArguments>(arguments, events, stop);
        _calls.Writer.TryWrite(call);
        await call.End.WaitAsync(stop);
    }
}

public sealed class FakeDiscovery : ScriptedPort<string>, IDiscovery
{
    public Task ScanAsync(string serviceUuid, EventSink events, CancellationToken stop) =>
        Run(serviceUuid, events, stop);
}

public sealed record Advertisement(string ServiceUuid, byte[] Payload, ChannelReader<Notification> Outbound);

public sealed class FakeAdvertising : ScriptedPort<Advertisement>, IAdvertising
{
    public Task AdvertiseAsync(
        string serviceUuid,
        byte[] payload,
        ChannelReader<Notification> outbound,
        EventSink events,
        CancellationToken stop) =>
        Run(new Advertisement(serviceUuid, payload, outbound), events, stop);
}

public sealed record Dial(string DeviceId, ChannelReader<LinkRequest> Requests);

public sealed class FakeCentral : ScriptedPort<Dial>, ICentral
{
    public Task ConnectAsync(
        string deviceId,
        ChannelReader<LinkRequest> requests,
        EventSink events,
        CancellationToken stop) =>
        Run(new Dial(deviceId, requests), events, stop);
}

public sealed class FakeNotifications : ScriptedPort<NotificationSpec>, INotifications
{
    public Task PostAsync(NotificationSpec notification, EventSink events, CancellationToken stop) =>
        Run(notification, events, stop);
}

public sealed record Capture(string PreviewId, CameraPurpose Purpose);

public sealed class FakeCamera : ScriptedPort<Capture>, ICamera
{
    public Task CaptureAsync(string previewId, CameraPurpose purpose, EventSink events, CancellationToken stop) =>
        Run(new Capture(previewId, purpose), events, stop);
}

/// <summary>A backend that never ends and never looks at its cancellation.</summary>
public sealed class DeafDiscovery : IDiscovery
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Started => _started.Task.WaitAsync(Wait.Limit);

    public Task ScanAsync(string serviceUuid, EventSink events, CancellationToken stop)
    {
        _started.TrySetResult();
        return _never.Task;
    }
}

/// <summary>A tag that answers each APDU with a scripted answer or failure.</summary>
public sealed class FakeCard : INfcCard
{
    private readonly Queue<Func<byte[], byte[]>> _answers = new();
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<byte[]> Transmitted { get; } = new();

    /// <summary>Completes when the session let go of the tag.</summary>
    public Task Released => _released.Task.WaitAsync(Wait.Limit);

    public FakeCard Answers(params byte[][] answers)
    {
        foreach (byte[] answer in answers)
        {
            _answers.Enqueue(_ => answer);
        }
        return this;
    }

    public FakeCard ThenFails(Exception failure)
    {
        _answers.Enqueue(_ => throw failure);
        return this;
    }

    public byte[] Transmit(byte[] apdu)
    {
        lock (Transmitted)
        {
            Transmitted.Add(apdu);
        }
        return _answers.Dequeue()(apdu);
    }

    public void Dispose() => _released.TrySetResult();
}

public sealed class FakeReader : INfcReader
{
    private readonly TaskCompletionSource<INfcCard> _tag = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _abandoned = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the session waits for a tag.</summary>
    public Task Waiting => _waiting.Task.WaitAsync(Wait.Limit);

    /// <summary>Completes when the session gave up the wait.</summary>
    public Task Abandoned => _abandoned.Task.WaitAsync(Wait.Limit);

    public void TagArrives(INfcCard card) => _tag.TrySetResult(card);

    public void Fails(Exception failure) => _tag.TrySetException(failure);

    public async Task<INfcCard> WaitForTagAsync(CancellationToken stop)
    {
        _waiting.TrySetResult();
        try
        {
            return await _tag.Task.WaitAsync(stop);
        }
        catch (OperationCanceledException)
        {
            _abandoned.TrySetResult();
            throw;
        }
    }
}

/// <summary>A direct exchange whose outcome the test sets.</summary>
public sealed class FakeDirect : IDirectExchange
{
    private readonly Func<DirectEndpoint, byte[], bool, byte[]> _outcome;

    public FakeDirect(Func<DirectEndpoint, byte[], bool, byte[]> outcome) => _outcome = outcome;

    public byte[] Exchange(DirectEndpoint endpoint, byte[] payload, bool isInitiator, CancellationToken stop) =>
        _outcome(endpoint, payload, isInitiator);
}

/// <summary>A clock the test moves; timers fire when it passes them.</summary>
public sealed class ManualTime : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = new();
    private long _now;

    public override long TimestampFrequency => 1000;

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public void Advance(int milliseconds)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += milliseconds;
            due = _timers.FindAll(timer => timer.Due <= _now);
            _timers.RemoveAll(timer => timer.Due <= _now);
        }
        due.ForEach(timer => timer.Fire());
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            var timer = new ManualTimer(callback, state, _now + (long)dueTime.TotalMilliseconds);
            _timers.Add(timer);
            return timer;
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, long due) : ITimer
    {
        internal long Due { get; } = due;

        internal void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public static class Wait
{
    /// <summary>
    /// How long a test waits for something that must happen. It is a bound
    /// on a hang, not a pause: nothing here waits for time to pass.
    /// </summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    public static async Task<Event> Next(this HardwareShell shell)
    {
        using var limit = new CancellationTokenSource(Limit);
        return await shell.NextAsync(limit.Token) ?? throw new TimeoutException("the shell reported nothing");
    }

    /// <summary>An event as the contract writes it: records holding arrays are equal only to themselves.</summary>
    public static string Wire(this Event observation) => Encoding.UTF8.GetString(ContractJson.EncodeEvent(observation));
}
