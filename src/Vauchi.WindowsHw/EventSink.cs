// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Threading;
using System.Threading.Tasks;
using Vauchi.Contract;

namespace Vauchi.WindowsHw;

public enum SendOutcome
{
    /// <summary>The observation is queued for Core.</summary>
    Accepted,

    /// <summary>The operation was stopped or the shell closed: end the native work.</summary>
    Ended,

    /// <summary>The observation is over the byte budget or cannot be written; it was dropped.</summary>
    Unfit,
}

/// <summary>
/// Where one operation's backend reports. A full inbox is backpressure:
/// the send waits, holding its one observation, until the host has taken
/// an event. A backend sends one observation at a time.
/// </summary>
public sealed class EventSink
{
    private readonly Operation _operation;
    private readonly Signal _capacity;
    private readonly Signal _available;

    internal EventSink(Operation operation, Signal capacity, Signal available)
    {
        _operation = operation;
        _capacity = capacity;
        _available = available;
    }

    public async ValueTask<SendOutcome> SendAsync(Event observation, CancellationToken stop)
    {
        while (true)
        {
            // Taken before publishing: a host that makes room between the
            // refusal and the wait has already completed this task.
            Task room = _capacity.NextAsync();
            switch (_operation.Publish(observation))
            {
                case null:
                    _available.Set();
                    return SendOutcome.Accepted;
                case DeliveryError.Full:
                    try
                    {
                        await room.WaitAsync(stop).ConfigureAwait(false);
                    }
                    catch (System.OperationCanceledException)
                    {
                        return SendOutcome.Ended;
                    }
                    break;
                case DeliveryError.PayloadTooLarge or DeliveryError.InvalidEvent:
                    return SendOutcome.Unfit;
                default:
                    return SendOutcome.Ended;
            }
        }
    }
}

/// <summary>Wakes whoever waits for the next <see cref="Set"/>.</summary>
internal sealed class Signal
{
    private readonly object _gate = new();
    private TaskCompletionSource _next = New();

    /// <summary>A task that completes at the next <see cref="Set"/> after this call.</summary>
    internal Task NextAsync()
    {
        lock (_gate)
        {
            return _next.Task;
        }
    }

    internal void Set()
    {
        TaskCompletionSource reached;
        lock (_gate)
        {
            reached = _next;
            _next = New();
        }
        reached.TrySetResult();
    }

    private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
