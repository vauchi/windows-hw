// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vauchi.Contract;

namespace Vauchi.WindowsHw;

/// <summary>
/// One port's operations, one at a time: starting another stops the one
/// running. A stopped operation's queued observations are dropped and its
/// late callbacks refused; one that ends by itself keeps what it queued
/// (the contract's finish and cancel).
/// </summary>
internal sealed class PortSession
{
    private readonly object _gate = new();
    private readonly EventInbox _inbox;
    private readonly Signal _capacity = new();
    private readonly Signal _available;
    private readonly string _transport;
    private readonly string _failure;
    private readonly List<Task> _running = new();
    private Operation? _operation;
    private CancellationTokenSource? _stop;

    /// <param name="transport">The transport name Core knows the port by.</param>
    /// <param name="failure">What a failed OS call is reported as: fixed text, never the OS's own.</param>
    internal PortSession(InboxLimits limits, Signal available, string transport, string failure)
    {
        _inbox = new EventInbox(limits);
        _available = available;
        _transport = transport;
        _failure = failure;
    }

    /// <summary>Null when the operation started; the inbox's refusal otherwise.</summary>
    internal DeliveryError? Start(Func<EventSink, CancellationToken, Task> run)
    {
        lock (_gate)
        {
            StopRunning();
            if (!_inbox.TryStart(out Operation? operation, out DeliveryError? refusal))
            {
                return refusal;
            }
            var stop = new CancellationTokenSource();
            var events = new EventSink(operation!, _capacity, _available);
            _operation = operation;
            _stop = stop;
            _running.RemoveAll(task => task.IsCompleted);
            _running.Add(Task.Run(() => RunAsync(run, events, operation!, stop.Token)));
            return null;
        }
    }

    internal void Stop()
    {
        lock (_gate)
        {
            StopRunning();
        }
    }

    internal Event? Pop()
    {
        Event? observation = _inbox.Pop();
        if (observation is not null)
        {
            _capacity.Set();
        }
        return observation;
    }

    /// <summary>
    /// Refuses every producer and stops what runs. The task completes when
    /// the port's work has returned; a port that ignores its stop never
    /// completes it, so the caller bounds the wait.
    /// </summary>
    internal Task Shutdown()
    {
        lock (_gate)
        {
            StopRunning();
            _inbox.Close();
            Task[] running = _running.ToArray();
            _running.Clear();
            // RunAsync lets no exception out, so waiting on this cannot throw.
            return Task.WhenAll(running);
        }
    }

    private void StopRunning()
    {
        // The inbox refuses the operation at once. Its native work is told
        // from a pool thread: Cancel runs the port's continuations inline,
        // and those must not run on the host's thread or under this lock.
        _operation?.Cancel();
        if (_stop is { } stop)
        {
            _running.Add(Task.Run(() => Cancel(stop)));
        }
        _stop = null;
        _operation = null;
    }

    private static void Cancel(CancellationTokenSource stop)
    {
        try
        {
            stop.Cancel();
        }
        catch (AggregateException)
        {
            // A port's cancellation callback threw; its operation is over either way.
        }
    }

    private async Task RunAsync(
        Func<EventSink, CancellationToken, Task> run,
        EventSink events,
        Operation operation,
        CancellationToken stop)
    {
        Event? failed = null;
        try
        {
            await run(events, stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return;
        }
        catch (HardwareFailureException failure)
        {
            failed = Report(failure.Failure);
        }
        catch (Exception)
        {
            failed = Report(HardwareFailure.Failed);
        }
        if (failed is not null)
        {
            await events.SendAsync(failed, stop).ConfigureAwait(false);
        }
        lock (_gate)
        {
            operation.Finish();
            // A later stop cancels the running operation, and cancelling
            // drops what it queued: an operation that ended by itself must
            // keep its last observations for the host.
            if (ReferenceEquals(_operation, operation))
            {
                _operation = null;
                _stop = null;
            }
        }
    }

    private Event Report(HardwareFailure failure) => failure switch
    {
        HardwareFailure.Unavailable => new Event.HardwareUnavailable { Transport = _transport },
        HardwareFailure.PermissionDenied => new Event.PermissionDenied { Transport = _transport },
        _ => new Event.HardwareError { Transport = _transport, Error = _failure },
    };
}
