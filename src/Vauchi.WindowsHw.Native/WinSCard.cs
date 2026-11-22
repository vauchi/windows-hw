// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Vauchi.WindowsHw.Native;

/// <summary>
/// The smart-card service of Windows, <c>winscard.dll</c>, as the reader
/// port's <see cref="IPcsc"/>. Each method is one call and its status
/// code; the order of calls and every decision are the port's.
/// </summary>
public sealed partial class WinSCard : IPcsc
{
    private const uint ScopeUser = 0;
    private const uint ShareShared = 2;
    private const uint ProtocolT0OrT1 = 3;
    private const uint LeaveCard = 0;
    private const uint StatePresent = 0x20;

    /// <summary>An extended APDU's longest answer, with its status word.</summary>
    private const int MaxAnswer = 65_538;

    public int Establish(out nint context) => SCardEstablishContext(ScopeUser, 0, 0, out context);

    public int ListReaders(nint context, out IReadOnlyList<string> readers)
    {
        readers = Array.Empty<string>();
        uint length = 0;
        int status = SCardListReadersW(context, null, null, ref length);
        if (status != 0)
        {
            return status;
        }
        char[] names = new char[length];
        status = SCardListReadersW(context, null, names, ref length);
        if (status != 0)
        {
            return status;
        }
        // A multi-string: each name ends in a NUL, the list in a second one.
        readers = new string(names, 0, (int)length).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return 0;
    }

    public unsafe int CardPresent(nint context, string reader, out bool present)
    {
        present = false;
        nint name = Marshal.StringToHGlobalUni(reader);
        try
        {
            // Current state "unaware" makes the call answer at once with what is there.
            var state = new ReaderState { Reader = name };
            int status = SCardGetStatusChangeW(context, 0, ref state, 1);
            present = status == 0 && (state.EventState & StatePresent) != 0;
            return status;
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }
    }

    public int Connect(nint context, string reader, out nint card, out uint protocol) =>
        SCardConnectW(context, reader, ShareShared, ProtocolT0OrT1, out card, out protocol);

    public int Transmit(nint card, uint protocol, byte[] apdu, out byte[] answer)
    {
        ArgumentNullException.ThrowIfNull(apdu);
        var request = new IoRequest { Protocol = protocol, Length = (uint)Marshal.SizeOf<IoRequest>() };
        byte[] buffer = new byte[MaxAnswer];
        uint length = (uint)buffer.Length;
        int status = SCardTransmit(card, ref request, apdu, (uint)apdu.Length, 0, buffer, ref length);
        answer = status == 0 ? buffer.AsSpan(0, (int)length).ToArray() : Array.Empty<byte>();
        return status;
    }

    public void Disconnect(nint card) => _ = SCardDisconnect(card, LeaveCard);

    public void Release(nint context) => _ = SCardReleaseContext(context);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ReaderState
    {
        public nint Reader;
        public nint UserData;
        public uint CurrentState;
        public uint EventState;
        public uint AtrLength;
        public fixed byte Atr[36];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoRequest
    {
        public uint Protocol;
        public uint Length;
    }

    [LibraryImport("winscard.dll")]
    private static partial int SCardEstablishContext(uint scope, nint reserved1, nint reserved2, out nint context);

    [LibraryImport("winscard.dll")]
    private static partial int SCardReleaseContext(nint context);

    [LibraryImport("winscard.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SCardListReadersW(nint context, string? groups, [Out] char[]? readers, ref uint length);

    [LibraryImport("winscard.dll")]
    private static partial int SCardGetStatusChangeW(nint context, uint timeoutMs, ref ReaderState states, uint count);

    [LibraryImport("winscard.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SCardConnectW(
        nint context,
        string reader,
        uint shareMode,
        uint preferredProtocols,
        out nint card,
        out uint activeProtocol);

    [LibraryImport("winscard.dll")]
    private static partial int SCardTransmit(
        nint card,
        ref IoRequest sendRequest,
        byte[] send,
        uint sendLength,
        nint receiveRequest,
        [Out] byte[] receive,
        ref uint receiveLength);

    [LibraryImport("winscard.dll")]
    private static partial int SCardDisconnect(nint card, uint disposition);
}
