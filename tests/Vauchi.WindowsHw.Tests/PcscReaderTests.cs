// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vauchi.Contract;
using Xunit;

namespace Vauchi.WindowsHw.Tests;

/// <summary>
/// The NFC reader port over PC/SC, against a scripted smart-card service:
/// CI has no reader, and the port's order of calls is the part that can be
/// wrong without one.
/// </summary>
public class PcscReaderTests
{
    /// <summary>Short, so a test that waits for a card does not wait on the clock.</summary>
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(1);

    private const int NoService = unchecked((int)0x8010001D);
    private const int NoReaders = unchecked((int)0x8010002E);
    private const int RemovedCard = unchecked((int)0x80100069);
    private const int Unresponsive = unchecked((int)0x80100066);

    /// <summary>A smart-card service whose readers, cards and failures the test sets.</summary>
    private sealed class FakePcsc : IPcsc
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource _polled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Readers { get; } = new();

        public HashSet<string> WithCard { get; } = new();

        public int EstablishResult { get; set; }

        public int ConnectResult { get; set; }

        public Queue<(int Result, byte[] Answer)> Answers { get; } = new();

        public List<string> Calls { get; } = new();

        public List<byte[]> Sent { get; } = new();

        /// <summary>Completes once the port has asked every reader at least once.</summary>
        public Task Polled => _polled.Task.WaitAsync(Wait.Limit);

        public void InsertCard(string reader)
        {
            lock (_gate)
            {
                WithCard.Add(reader);
            }
        }

        public int Establish(out nint context)
        {
            Record("establish");
            context = EstablishResult == 0 ? 7 : 0;
            return EstablishResult;
        }

        public int ListReaders(nint context, out IReadOnlyList<string> readers)
        {
            Record("list");
            readers = Readers.ToArray();
            return Readers.Count == 0 ? NoReaders : 0;
        }

        public int CardPresent(nint context, string reader, out bool present)
        {
            lock (_gate)
            {
                present = WithCard.Contains(reader);
                if (reader == Readers[^1])
                {
                    _polled.TrySetResult();
                }
            }
            return 0;
        }

        public int Connect(nint context, string reader, out nint card, out uint protocol)
        {
            Record($"connect {reader}");
            card = ConnectResult == 0 ? 11 : 0;
            protocol = 2;
            return ConnectResult;
        }

        public int Transmit(nint card, uint protocol, byte[] apdu, out byte[] answer)
        {
            Record("transmit");
            lock (_gate)
            {
                Sent.Add(apdu);
            }
            (int result, byte[] scripted) = Answers.Dequeue();
            answer = scripted;
            return result;
        }

        public void Disconnect(nint card) => Record("disconnect");

        public void Release(nint context) => Record("release");

        private void Record(string call)
        {
            lock (_gate)
            {
                Calls.Add(call);
            }
        }
    }

    private static FakePcsc OneReader(bool withCard = true)
    {
        var pcsc = new FakePcsc();
        pcsc.Readers.Add("ACR122 0");
        if (withCard)
        {
            pcsc.WithCard.Add("ACR122 0");
        }
        return pcsc;
    }

    private static async Task<string> Refusal(Func<Task> act) =>
        (await Assert.ThrowsAsync<NfcException>(act)).Message;

    [Fact]
    public async Task ACardInTheReaderIsConnectedAndItsAnswersComeBackUnchanged()
    {
        FakePcsc pcsc = OneReader();
        pcsc.Answers.Enqueue((0, new byte[] { 0x6A, 0x82 }));
        pcsc.Answers.Enqueue((0, new byte[] { 1, 2, 3, 0x90, 0x00 }));

        using INfcCard card = await new PcscReader(pcsc, Pause).WaitForTagAsync(default);
        byte[] first = card.Transmit(new byte[] { 0x00, 0xA4 });
        byte[] second = card.Transmit(new byte[] { 0x80, 0x10 });

        Assert.Equal(new byte[] { 0x6A, 0x82 }, first);
        Assert.Equal(new byte[] { 1, 2, 3, 0x90, 0x00 }, second);
        Assert.Equal(new[] { new byte[] { 0x00, 0xA4 }, new byte[] { 0x80, 0x10 } }, pcsc.Sent);
        Assert.Equal(new[] { "establish", "list", "connect ACR122 0", "transmit", "transmit" }, pcsc.Calls);
    }

    [Fact]
    public async Task TheWaitGoesOnUntilACardArrivesAndTakesTheReaderThatHasIt()
    {
        var pcsc = new FakePcsc();
        pcsc.Readers.AddRange(new[] { "Contact 0", "ACR122 0" });

        Task<INfcCard> waiting = new PcscReader(pcsc, Pause).WaitForTagAsync(default);
        await pcsc.Polled;
        Assert.False(waiting.IsCompleted);
        Assert.DoesNotContain(pcsc.Calls, call => call.StartsWith("connect", StringComparison.Ordinal));
        pcsc.InsertCard("ACR122 0");

        using INfcCard card = await waiting.WaitAsync(Wait.Limit);
        Assert.Contains("connect ACR122 0", pcsc.Calls);
        Assert.DoesNotContain("connect Contact 0", pcsc.Calls);
    }

    [Fact]
    public async Task StoppingEndsTheWaitAndGivesTheServiceItsContextBack()
    {
        FakePcsc pcsc = OneReader(withCard: false);
        using var stop = new CancellationTokenSource();

        Task<INfcCard> waiting = new PcscReader(pcsc, Pause).WaitForTagAsync(stop.Token);
        await pcsc.Polled;
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Wait.Limit));
        Assert.Equal("release", pcsc.Calls[^1]);
        Assert.Equal(1, pcsc.Calls.Count(call => call == "release"));
    }

    [Fact]
    public async Task LettingGoOfTheCardDisconnectsItAndReleasesTheContextOnce()
    {
        FakePcsc pcsc = OneReader();
        INfcCard card = await new PcscReader(pcsc, Pause).WaitForTagAsync(default);

        card.Dispose();
        card.Dispose();

        Assert.Equal(new[] { "establish", "list", "connect ACR122 0", "disconnect", "release" }, pcsc.Calls);
    }

    [Fact]
    public async Task AMachineWithNoSmartCardServiceSaysSo()
    {
        var pcsc = new FakePcsc { EstablishResult = NoService };

        Assert.Equal("SCARD_E_NO_SERVICE", await Refusal(() => new PcscReader(pcsc, Pause).WaitForTagAsync(default)));
        Assert.Equal(new[] { "establish" }, pcsc.Calls);
    }

    [Fact]
    public async Task AMachineWithNoReaderSaysSoAndReleasesTheContext()
    {
        var pcsc = new FakePcsc();

        Assert.Equal(
            "SCARD_E_NO_READERS_AVAILABLE",
            await Refusal(() => new PcscReader(pcsc, Pause).WaitForTagAsync(default)));
        Assert.Equal(new[] { "establish", "list", "release" }, pcsc.Calls);
    }

    [Fact]
    public async Task ACardThatCannotBeConnectedIsReportedByTheServicesNameForIt()
    {
        FakePcsc pcsc = OneReader();
        pcsc.ConnectResult = Unresponsive;

        Assert.Equal(
            "SCARD_W_UNRESPONSIVE_CARD",
            await Refusal(() => new PcscReader(pcsc, Pause).WaitForTagAsync(default)));
        Assert.Equal("release", pcsc.Calls[^1]);
    }

    [Fact]
    public async Task ACardRemovedDuringAnExchangeIsReportedByName()
    {
        FakePcsc pcsc = OneReader();
        pcsc.Answers.Enqueue((RemovedCard, Array.Empty<byte>()));
        using INfcCard card = await new PcscReader(pcsc, Pause).WaitForTagAsync(default);

        var refusal = Assert.Throws<NfcException>(() => card.Transmit(new byte[] { 0x00 }));

        Assert.Equal("SCARD_W_REMOVED_CARD", refusal.Message);
    }

    [Fact]
    public async Task AnErrorWithNoNameIsReportedAsItsCodeAndNothingElse()
    {
        FakePcsc pcsc = OneReader();
        pcsc.Answers.Enqueue((unchecked((int)0x8010FFFF), new byte[] { 0xDE, 0xAD }));
        using INfcCard card = await new PcscReader(pcsc, Pause).WaitForTagAsync(default);

        var refusal = Assert.Throws<NfcException>(() => card.Transmit(new byte[] { 0x00 }));

        Assert.Equal("SCARD 0x8010FFFF", refusal.Message);
    }

    [Fact]
    public async Task ThroughTheShellACardsAnswersReachCoreAsApduEvents()
    {
        FakePcsc pcsc = OneReader();
        pcsc.Answers.Enqueue((0, new byte[] { 0x90, 0x00 }));
        pcsc.Answers.Enqueue((RemovedCard, Array.Empty<byte>()));
        await using var shell = new HardwareShell(
            new Backends { Nfc = new PcscReader(pcsc, Pause) },
            new DeviceCapabilities { HasNfc = true });
        var select = new byte[] { 0x00, 0xA4, 0x04, 0x00 };

        shell.Execute(new Command.NfcActivate { Payload = Array.Empty<byte>(), Apdus = new[] { select, select } });

        Assert.Equal("{\"NfcApduReceived\":{\"bytes\":[144,0]}}", (await shell.Next()).Wire());
        Assert.Equal("{\"NfcFailed\":{\"reason\":\"SCARD_W_REMOVED_CARD\"}}", (await shell.Next()).Wire());
    }
}
