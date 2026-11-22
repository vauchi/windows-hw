// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vauchi.Contract;
using Xunit;

namespace Vauchi.WindowsHw.Tests;

/// <summary>
/// The helper's conversation over a pair of anonymous pipes, the channel
/// the terminal gives it. The test is the terminal.
/// </summary>
public sealed class HostLoopTests : IAsyncDisposable
{
    private readonly AnonymousPipeServerStream _toHost = new(PipeDirection.Out);
    private readonly AnonymousPipeServerStream _fromHost = new(PipeDirection.In);
    private readonly AnonymousPipeClientStream _hostInput;
    private readonly AnonymousPipeClientStream _hostOutput;
    private readonly StreamReader _lines;
    private readonly FakeCentral _central = new();
    private readonly HardwareShell _shell;
    private readonly Task _host;

    public HostLoopTests()
    {
        _hostInput = new AnonymousPipeClientStream(PipeDirection.In, _toHost.ClientSafePipeHandle);
        _hostOutput = new AnonymousPipeClientStream(PipeDirection.Out, _fromHost.ClientSafePipeHandle);
        _lines = new StreamReader(_fromHost, Encoding.UTF8);
        _shell = new HardwareShell(
            new Backends { Central = _central, Direct = new FakeDirect((_, payload, _) => payload) },
            new DeviceCapabilities { HasInternet = true, Platform = Platform.Desktop });
        _host = Task.Run(() => HostLoop.RunAsync(_shell, _hostInput, _hostOutput, CancellationToken.None));
    }

    public async ValueTask DisposeAsync()
    {
        _toHost.Dispose();
        await _host.WaitAsync(Wait.Limit);
        await _shell.DisposeAsync();
        _hostInput.Dispose();
        _hostOutput.Dispose();
        _lines.Dispose();
    }

    private void Send(string line) => SendBytes(Encoding.UTF8.GetBytes(line + "\n"));

    private void SendBytes(byte[] bytes)
    {
        _toHost.Write(bytes);
        _toHost.Flush();
    }

    private async Task<string> Line() =>
        await _lines.ReadLineAsync().WaitAsync(Wait.Limit) ?? throw new EndOfStreamException("the helper closed its output");

    private static string Refused(int line, string reason) =>
        "{\"refused\":{\"line\":" + line + ",\"reason\":\"" + reason + "\"}}";

    [Fact]
    public async Task TheFirstLineIsTheCapabilityReport()
    {
        using JsonDocument first = JsonDocument.Parse(await Line());

        JsonElement capabilities = first.RootElement.GetProperty("event")
            .GetProperty("HardwareCapabilitiesReported").GetProperty("capabilities");
        Assert.True(capabilities.GetProperty("has_internet").GetBoolean());
        Assert.False(capabilities.GetProperty("has_ble").GetBoolean());
        Assert.Equal("Desktop", capabilities.GetProperty("platform").GetString());
    }

    [Fact]
    public async Task ACommandReachesItsPortAndWhatThePortObservesComesBackAsAnEventLine()
    {
        await Line();

        Send("{\"BleConnect\":{\"device_id\":\"peer-1\"}}");
        Call<Dial> dial = await _central.NextCall();
        await dial.Send(new Event.BleConnected { DeviceId = "peer-1", Direction = BleLinkDirection.Outbound });

        Assert.Equal("peer-1", dial.Arguments.DeviceId);
        Assert.Equal(
            "{\"event\":{\"BleConnected\":{\"device_id\":\"peer-1\",\"direction\":\"Outbound\"}}}",
            await Line());
    }

    [Fact]
    public async Task ACommandTheShellTakesProducesNoLineOfItsOwn()
    {
        await Line();

        Send("{\"DirectSend\":{\"payload\":[7],\"is_initiator\":true}}");

        Assert.Equal("{\"event\":{\"DirectPayloadReceived\":{\"data\":[7]}}}", await Line());
    }

    [Theory]
    [InlineData("{\"AudioStop\":null}", "malformed")]
    [InlineData("\"AudioStop\"", "unsupported")]
    [InlineData("{\"QrDisplay\":{\"data\":\"a display command\"}}", "unsupported")]
    [InlineData("{\"BleDisconnect\":{\"device_id\":\"nobody\",\"direction\":\"Outbound\"}}", "inactive")]
    [InlineData("{\"DirectSend\":{\"payload\":[],\"is_initiator\":true}}", "too_large")]
    [InlineData("not json", "malformed")]
    [InlineData("{\"BleConnect\":{\"device_id\":7}}", "malformed")]
    [InlineData("{\"BleConnect\":{}}", "malformed")]
    [InlineData("{\"UnknownCommand\":{}}", "malformed")]
    [InlineData("{\"BleConnect\":{\"device_id\":\"p\"}} trailing", "malformed")]
    [InlineData("", "malformed")]
    [InlineData("\r", "malformed")]
    [InlineData("   ", "malformed")]
    [InlineData("\u0000", "malformed")]
    public async Task ALineTheShellDoesNotTakeIsRefusedWithItsNumberAndAReason(string line, string reason)
    {
        await Line();

        Send(line);

        Assert.Equal(Refused(1, reason), await Line());
    }

    [Fact]
    public async Task ARefusalCountsEveryInputLineAndLaterCommandsStillRun()
    {
        await Line();

        Send("garbage");
        Send("{\"DirectSend\":{\"payload\":[1],\"is_initiator\":true}}");
        Send("more garbage");

        string[] lines = { await Line(), await Line(), await Line() };
        Assert.Contains(Refused(1, "malformed"), lines);
        Assert.Contains("{\"event\":{\"DirectPayloadReceived\":{\"data\":[1]}}}", lines);
        Assert.Contains(Refused(3, "malformed"), lines);
    }

    [Fact]
    public async Task ALineOverTheBoundIsRefusedWithoutBeingReadIntoMemoryAndTheNextLineRuns()
    {
        await Line();
        byte[] filler = Encoding.UTF8.GetBytes(new string('x', 64 * 1024));
        Task writing = Task.Run(() =>
        {
            for (int written = 0; written <= HostLoop.MaxLineBytes; written += filler.Length)
            {
                _toHost.Write(filler);
            }
            SendBytes(Encoding.UTF8.GetBytes("\n{\"DirectSend\":{\"payload\":[2],\"is_initiator\":true}}\n"));
        });

        Assert.Equal(Refused(1, "line_too_long"), await Line());
        Assert.Equal("{\"event\":{\"DirectPayloadReceived\":{\"data\":[2]}}}", await Line());
        await writing.WaitAsync(Wait.Limit);
    }

    [Fact]
    public async Task ALineOfExactlyTheBoundIsRead()
    {
        await Line();
        string prefix = "{\"QrDisplay\":{\"data\":\"";
        string suffix = "\"}}";
        string line = prefix + new string('x', HostLoop.MaxLineBytes - prefix.Length - suffix.Length) + suffix;
        Task writing = Task.Run(() => Send(line));

        Assert.Equal(Refused(1, "unsupported"), await Line());
        await writing.WaitAsync(Wait.Limit);
    }

    [Fact]
    public async Task AWindowsLineEndingIsNotPartOfTheCommand()
    {
        await Line();

        SendBytes(Encoding.UTF8.GetBytes("{\"DirectSend\":{\"payload\":[3],\"is_initiator\":true}}\r\n"));

        Assert.Equal("{\"event\":{\"DirectPayloadReceived\":{\"data\":[3]}}}", await Line());
    }

    [Fact]
    public async Task ACommandCutOffByTheEndOfInputIsNotRun()
    {
        await Line();
        SendBytes(Encoding.UTF8.GetBytes("{\"BleConnect\":{\"device_id\":\"peer-1\"}}"));

        _toHost.Dispose();
        await _host.WaitAsync(Wait.Limit);

        Assert.False(_central.HasCall);
    }

    [Fact]
    public async Task TheEndOfInputEndsTheLoopAndStopsWhatWasRunning()
    {
        await Line();
        Send("{\"BleConnect\":{\"device_id\":\"peer-1\"}}");
        Call<Dial> dial = await _central.NextCall();

        _toHost.Dispose();
        await _host.WaitAsync(Wait.Limit);
        await _shell.DisposeAsync();

        await dial.Stopped;
    }

    [Fact]
    public async Task ATerminalThatStopsReadingDoesNotCrashTheHelper()
    {
        await Line();
        Send("{\"DirectSend\":{\"payload\":[4],\"is_initiator\":true}}");
        await Line();

        _fromHost.Dispose();
        Send("{\"DirectSend\":{\"payload\":[5],\"is_initiator\":true}}");
        Send("a line whose refusal has nowhere to go");
        Send("and another");
        _toHost.Dispose();

        await _host.WaitAsync(Wait.Limit);
        Assert.True(_host.IsCompletedSuccessfully);
    }
}
