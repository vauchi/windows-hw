// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Vauchi.WindowsHw;

/// <summary>
/// The smart-card service as the reader port uses it: PC/SC's calls with
/// their status code as the return value, zero for success. The Windows
/// implementation is WinSCard; a test scripts one.
/// </summary>
public interface IPcsc
{
    int Establish(out nint context);

    int ListReaders(nint context, out IReadOnlyList<string> readers);

    /// <summary>Whether a card is in <paramref name="reader"/> now; it does not wait.</summary>
    int CardPresent(nint context, string reader, out bool present);

    int Connect(nint context, string reader, out nint card, out uint protocol);

    int Transmit(nint card, uint protocol, byte[] apdu, out byte[] answer);

    void Disconnect(nint card);

    void Release(nint context);
}

/// <summary>
/// The NFC reader port over PC/SC, as on Linux (owner, 2026-10-31): wait
/// for a card on any reader, connect, and carry Core's APDUs unchanged.
/// PC/SC has no arrival callback, so the wait asks each reader in turn. A
/// failure is reported by the service's own name for it, which holds no
/// card content.
/// </summary>
public sealed class PcscReader : INfcReader
{
    /// <summary>The pause after every reader was asked and none had a card; also how soon a stop is noticed.</summary>
    public static readonly TimeSpan DefaultPause = TimeSpan.FromMilliseconds(250);

    private readonly IPcsc _pcsc;
    private readonly TimeSpan _pause;

    public PcscReader(IPcsc pcsc, TimeSpan? pause = null)
    {
        ArgumentNullException.ThrowIfNull(pcsc);
        _pcsc = pcsc;
        _pause = pause ?? DefaultPause;
    }

    public Task<INfcCard> WaitForTagAsync(CancellationToken stop) => Task.Run(() => Wait(stop), stop);

    private INfcCard Wait(CancellationToken stop)
    {
        Check(_pcsc.Establish(out nint context));
        try
        {
            Check(_pcsc.ListReaders(context, out IReadOnlyList<string> readers));
            while (true)
            {
                foreach (string reader in readers)
                {
                    stop.ThrowIfCancellationRequested();
                    Check(_pcsc.CardPresent(context, reader, out bool present));
                    if (!present)
                    {
                        continue;
                    }
                    Check(_pcsc.Connect(context, reader, out nint card, out uint protocol));
                    return new Card(_pcsc, context, card, protocol);
                }
                // Signalled means stopped: the next round's check throws.
                stop.WaitHandle.WaitOne(_pause);
                stop.ThrowIfCancellationRequested();
            }
        }
        catch
        {
            _pcsc.Release(context);
            throw;
        }
    }

    private static void Check(int status)
    {
        if (status != 0)
        {
            throw new NfcException(PcscStatus.Name(status));
        }
    }

    private sealed class Card(IPcsc pcsc, nint context, nint card, uint protocol) : INfcCard
    {
        private int _released;

        public byte[] Transmit(byte[] apdu)
        {
            Check(pcsc.Transmit(card, protocol, apdu, out byte[] answer));
            return answer;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                pcsc.Disconnect(card);
                pcsc.Release(context);
            }
        }
    }
}

/// <summary>PC/SC's names for its status codes (<c>winerror.h</c>, <c>pcsclite.h</c>).</summary>
public static class PcscStatus
{
    public static string Name(int status) => unchecked((uint)status) switch
    {
        0x80100002 => "SCARD_E_CANCELLED",
        0x80100009 => "SCARD_E_UNKNOWN_READER",
        0x8010000A => "SCARD_E_TIMEOUT",
        0x8010000B => "SCARD_E_SHARING_VIOLATION",
        0x8010000C => "SCARD_E_NO_SMARTCARD",
        0x8010000F => "SCARD_E_PROTO_MISMATCH",
        0x80100016 => "SCARD_E_NOT_TRANSACTED",
        0x80100017 => "SCARD_E_READER_UNAVAILABLE",
        0x8010001D => "SCARD_E_NO_SERVICE",
        0x8010001E => "SCARD_E_SERVICE_STOPPED",
        0x8010002E => "SCARD_E_NO_READERS_AVAILABLE",
        0x80100065 => "SCARD_W_UNSUPPORTED_CARD",
        0x80100066 => "SCARD_W_UNRESPONSIVE_CARD",
        0x80100067 => "SCARD_W_UNPOWERED_CARD",
        0x80100068 => "SCARD_W_RESET_CARD",
        0x80100069 => "SCARD_W_REMOVED_CARD",
        uint other => "SCARD 0x" + other.ToString("X8", CultureInfo.InvariantCulture),
    };
}
