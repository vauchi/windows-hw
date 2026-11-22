// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Vauchi.Contract;

namespace Vauchi.WindowsHw;

/// <summary>
/// The NFC reader role. <c>NfcActivate</c> starts one operation: wait for a
/// tag, transmit Core's frames, then keep the tag for the frames of later
/// <c>NfcSendApdu</c>s. <c>NfcDeactivate</c> ends the wait or the
/// connection. Each answer is an <c>NfcApduReceived</c>, each failure an
/// <c>NfcFailed</c>; nothing here reads a status word.
/// </summary>
internal sealed class NfcSession(PortSession session, INfcReader reader)
{
    private const int MaxQueuedRequests = 64;
    private const int MaxFramesPerRequest = 64;
    private const int MaxApdu = 65_535;
    private const string NoTag = "no NFC tag is connected";
    private const string ReaderFailed = "NFC reader failed";
    private const string AnswerTooLarge = "NFC answer exceeds executor limits";

    private Channel<IReadOnlyList<byte[]>>? _frames;

    internal PortSession Session { get; } = session;

    internal DeliveryError? Activate(IReadOnlyList<byte[]> apdus)
    {
        if (!Fit(apdus))
        {
            return DeliveryError.PayloadTooLarge;
        }
        Channel<IReadOnlyList<byte[]>> later = Channel.CreateBounded<IReadOnlyList<byte[]>>(MaxQueuedRequests);
        DeliveryError? refusal = Session.Start((events, stop) => ServeAsync(apdus, later, events, stop));
        _frames = refusal is null ? later : null;
        return refusal;
    }

    internal DeliveryError? Send(IReadOnlyList<byte[]> apdus)
    {
        if (!Fit(apdus))
        {
            return DeliveryError.PayloadTooLarge;
        }
        // A tag that failed or left completed the channel: Core learns
        // there is none, as it does with no activation at all.
        if (_frames is null || _frames.Reader.Completion.IsCompleted)
        {
            _frames = null;
            return Session.Start(async (events, stop) => await events.SendAsync(Failed(NoTag), stop).ConfigureAwait(false));
        }
        return _frames.Writer.TryWrite(apdus) ? null : DeliveryError.Full;
    }

    internal void Deactivate()
    {
        _frames = null;
        Session.Stop();
    }

    private static bool Fit(IReadOnlyList<byte[]> apdus) =>
        apdus.Count <= MaxFramesPerRequest && apdus.All(apdu => apdu.Length <= MaxApdu);

    private static Event Failed(string reason) => new Event.NfcFailed { Reason = reason };

    private async Task ServeAsync(
        IReadOnlyList<byte[]> first,
        Channel<IReadOnlyList<byte[]>> later,
        EventSink events,
        CancellationToken stop)
    {
        try
        {
            INfcCard card;
            try
            {
                card = await reader.WaitForTagAsync(stop).ConfigureAwait(false);
            }
            catch (NfcException failure)
            {
                await events.SendAsync(Failed(failure.Message), stop).ConfigureAwait(false);
                return;
            }
            using (card)
            {
                IReadOnlyList<byte[]> frames = first;
                while (await TransmitAsync(card, frames, events, stop).ConfigureAwait(false))
                {
                    frames = await later.Reader.ReadAsync(stop).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            later.Writer.TryComplete();
        }
    }

    /// <summary>False once the tag failed or the operation ended: the connection is over.</summary>
    private static async Task<bool> TransmitAsync(
        INfcCard card,
        IReadOnlyList<byte[]> frames,
        EventSink events,
        CancellationToken stop)
    {
        foreach (byte[] apdu in frames)
        {
            Event answer;
            bool tagLost = false;
            try
            {
                answer = new Event.NfcApduReceived { Bytes = card.Transmit(apdu) };
            }
            catch (NfcException failure)
            {
                answer = Failed(failure.Message);
                tagLost = true;
            }
            catch (Exception) when (!stop.IsCancellationRequested)
            {
                answer = Failed(ReaderFailed);
                tagLost = true;
            }
            SendOutcome sent = await events.SendAsync(answer, stop).ConfigureAwait(false);
            if (sent == SendOutcome.Unfit)
            {
                // Core must not wait for an answer the inbox refused.
                tagLost = true;
                sent = await events.SendAsync(Failed(AnswerTooLarge), stop).ConfigureAwait(false);
            }
            if (sent == SendOutcome.Ended || tagLost)
            {
                return false;
            }
        }
        return true;
    }
}
