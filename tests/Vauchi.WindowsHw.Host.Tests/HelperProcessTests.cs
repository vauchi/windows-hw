// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Vauchi.WindowsHw.Host.Tests;

/// <summary>
/// The built helper, started the way a terminal starts it: a child
/// process with its input and output redirected. The test is the
/// terminal.
/// </summary>
public sealed class HelperProcessTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    private readonly Process _helper;

    public HelperProcessTests()
    {
        // VAUCHI_HW_HOST names the published, ahead-of-time helper, the file
        // that ships; without it the build beside the tests is started.
        string? published = Environment.GetEnvironmentVariable("VAUCHI_HW_HOST");
        string program = string.IsNullOrEmpty(published)
            ? Path.Combine(AppContext.BaseDirectory, "vauchi-hw-host.exe")
            : published;
        Assert.True(File.Exists(program), $"{program} does not exist");
        _helper = Process.Start(new ProcessStartInfo(program)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        })!;
    }

    public void Dispose()
    {
        if (!_helper.HasExited)
        {
            _helper.Kill(entireProcessTree: true);
        }
        _helper.Dispose();
    }

    private void Send(string line)
    {
        _helper.StandardInput.Write(line + "\n");
        _helper.StandardInput.Flush();
    }

    private async Task<string> Line() =>
        await _helper.StandardOutput.ReadLineAsync().WaitAsync(Limit)
        ?? throw new EndOfStreamException("the helper closed its output");

    private async Task<int> ExitCode()
    {
        await _helper.WaitForExitAsync().WaitAsync(Limit);
        return _helper.ExitCode;
    }

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task ItSpeaksFirstWithWhatThisMachineHas()
    {
        using JsonDocument first = JsonDocument.Parse(await Line());

        JsonElement capabilities = first.RootElement.GetProperty("event")
            .GetProperty("HardwareCapabilitiesReported").GetProperty("capabilities");
        Assert.Equal("Desktop", capabilities.GetProperty("platform").GetString());
        Assert.True(capabilities.GetProperty("has_internet").GetBoolean());
        // Bluetooth and NFC are this machine's to answer: every backend for
        // them is wired. No camera backend is, so a camera is never claimed.
        Assert.Contains(capabilities.GetProperty("has_ble").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
        Assert.Contains(capabilities.GetProperty("has_nfc").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
        Assert.False(capabilities.GetProperty("has_camera").GetBoolean());
    }

    [Fact]
    public async Task ItRefusesWhatItCannotRunAndWhatIsNotACommandByLineNumber()
    {
        await Line();

        Send("\"AudioStop\"");
        Send("not a command");
        Send("{\"QrDisplay\":{\"data\":\"for the display shell\"}}");

        Assert.Equal("{\"refused\":{\"line\":1,\"reason\":\"unsupported\"}}", await Line());
        Assert.Equal("{\"refused\":{\"line\":2,\"reason\":\"malformed\"}}", await Line());
        Assert.Equal("{\"refused\":{\"line\":3,\"reason\":\"unsupported\"}}", await Line());
    }

    [Fact]
    public async Task ADirectSendToAPortNobodyListensOnComesBackAsAHardwareError()
    {
        await Line();
        string endpoint = "{\"fallback_host\":\"127.0.0.1\",\"port\":" + ClosedPort()
            + ",\"io_timeout_ms\":20000,\"browse_timeout_ms\":0,\"service_type\":\"_vauchi-exchange._tcp.local.\"}";

        Send("{\"DirectSend\":{\"payload\":[1,2,3],\"is_initiator\":true,\"endpoint\":" + endpoint + "}}");

        Assert.Equal(
            "{\"event\":{\"HardwareError\":{\"error\":\"TCP connect failed: ConnectionRefused\",\"transport\":\"USB\"}}}",
            await Line());
    }

    [Fact]
    public async Task ADirectSendReachesAPeerAndItsPayloadComesBack()
    {
        await Line();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<byte[]> peer = Task.Run(async () =>
        {
            using TcpClient connection = await listener.AcceptTcpClientAsync();
            NetworkStream link = connection.GetStream();
            byte[] received = Vauchi.Contract.DirectFrame.Read(link);
            Vauchi.Contract.DirectFrame.Write(link, new byte[] { 9 });
            return received;
        });
        string endpoint = "{\"fallback_host\":\"127.0.0.1\",\"port\":" + port
            + ",\"io_timeout_ms\":20000,\"browse_timeout_ms\":0,\"service_type\":\"_vauchi-exchange._tcp.local.\"}";

        Send("{\"DirectSend\":{\"payload\":[1,2,3],\"is_initiator\":true,\"endpoint\":" + endpoint + "}}");

        Assert.Equal("{\"event\":{\"DirectPayloadReceived\":{\"data\":[9]}}}", await Line());
        Assert.Equal(new byte[] { 1, 2, 3 }, await peer.WaitAsync(Limit));
        listener.Stop();
    }

    [Fact]
    public async Task AScanForAServiceNobodyAdvertisesIsTakenAndStopped()
    {
        await Line();

        Send("{\"BleStartScanning\":{\"service_uuid\":\"7f9a1b2c-3d4e-4f50-8a6b-7c8d9e0f1a2b\"}}");
        Send("\"BleStopScanning\"");
        Send("\"AudioStop\"");

        // Either the scan was taken silently, or this machine's radio is
        // off and says so; a refusal of the scan itself would be line 1.
        string next = await Line();
        if (next.Contains("HardwareUnavailable", StringComparison.Ordinal))
        {
            next = await Line();
        }
        Assert.Equal("{\"refused\":{\"line\":3,\"reason\":\"unsupported\"}}", next);
    }

    [Fact]
    public async Task ItExitsCleanlyWhenItsInputEnds()
    {
        await Line();
        Send("{\"BleStartScanning\":{\"service_uuid\":\"7f9a1b2c-3d4e-4f50-8a6b-7c8d9e0f1a2b\"}}");

        _helper.StandardInput.Close();

        Assert.Equal(0, await ExitCode());
        Assert.Equal("", await _helper.StandardError.ReadToEndAsync().WaitAsync(Limit));
    }

    [Fact]
    public async Task ItExitsWhenTheTerminalStopsReadingAndThenCloses()
    {
        await Line();

        _helper.StandardOutput.Close();
        Send("not a command");
        _helper.StandardInput.Close();

        Assert.Equal(0, await ExitCode());
    }
}
