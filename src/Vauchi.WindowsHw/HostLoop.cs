// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Buffers;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vauchi.Contract;

namespace Vauchi.WindowsHw;

/// <summary>
/// The helper process's conversation with the terminal that started it
/// (the bridge the maintainer selected on 2026-11-22, private#772). Both
/// directions are lines of UTF-8 JSON over the pipes the child inherited:
/// no name exists that another process could open.
///
/// In: one contract <c>Command</c> per line. Out: the capability report
/// first, then one line per observation, <c>{"event": ...}</c>, and one
/// per command the shell did not take, <c>{"refused": {"line": n,
/// "reason": "..."}}</c>, where <c>n</c> counts the input lines from 1.
/// A command the shell took produces no line of its own. Every input line
/// is untrusted: it is bounded before it is read and decoded by the
/// contract's own reader. The loop ends when the input ends, which is also
/// what a dead parent looks like.
/// </summary>
public static class HostLoop
{
    /// <summary>
    /// The longest command line read. The largest command a terminal's
    /// exchange sends is a direct payload of 64 KiB, about 260 KB as JSON.
    /// </summary>
    public const int MaxLineBytes = 1 << 20;

    private const byte LineFeed = (byte)'\n';

    public static async Task RunAsync(HardwareShell shell, Stream input, Stream output, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        using var ended = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var lines = new Lines(output);
        try
        {
            await lines.WriteAsync("event", ContractJson.EncodeEvent(shell.Report()), ended.Token).ConfigureAwait(false);
            Task observations = PumpAsync(shell, lines, ended.Token);
            try
            {
                await ReadAsync(shell, input, lines, ended.Token).ConfigureAwait(false);
            }
            finally
            {
                ended.Cancel();
                await observations.ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // The terminal closed its end; there is nobody left to tell.
        }
        catch (OperationCanceledException) when (ended.IsCancellationRequested)
        {
        }
    }

    private static async Task PumpAsync(HardwareShell shell, Lines lines, CancellationToken ended)
    {
        try
        {
            while (await shell.NextAsync(ended).ConfigureAwait(false) is { } observation)
            {
                await lines.WriteAsync("event", ContractJson.EncodeEvent(observation), ended).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task ReadAsync(HardwareShell shell, Stream input, Lines lines, CancellationToken ended)
    {
        var line = new ArrayBufferWriter<byte>();
        byte[] chunk = new byte[16 * 1024];
        long number = 1;
        bool tooLong = false;
        int read;
        while ((read = await input.ReadAsync(chunk, ended).ConfigureAwait(false)) > 0)
        {
            int start = 0;
            for (int index = 0; index < read; index++)
            {
                if (chunk[index] != LineFeed)
                {
                    continue;
                }
                tooLong |= !Append(line, chunk.AsSpan(start, index - start));
                await TakeAsync(shell, line, tooLong, number, lines, ended).ConfigureAwait(false);
                line.Clear();
                tooLong = false;
                number++;
                start = index + 1;
            }
            tooLong |= !Append(line, chunk.AsSpan(start, read - start));
        }
    }

    /// <summary>False once the line is over the bound; what is over is not kept.</summary>
    private static bool Append(ArrayBufferWriter<byte> line, ReadOnlySpan<byte> bytes)
    {
        if (line.WrittenCount + bytes.Length > MaxLineBytes)
        {
            return false;
        }
        line.Write(bytes);
        return true;
    }

    private static async Task TakeAsync(
        HardwareShell shell,
        ArrayBufferWriter<byte> line,
        bool tooLong,
        long number,
        Lines lines,
        CancellationToken ended)
    {
        string? reason = tooLong ? "line_too_long" : Execute(shell, line.WrittenSpan);
        if (reason is not null)
        {
            await lines.WriteAsync(Refusal(number, reason), ended).ConfigureAwait(false);
        }
    }

    /// <summary>Null when the shell took the command; why not otherwise.</summary>
    private static string? Execute(HardwareShell shell, ReadOnlySpan<byte> line)
    {
        // A carriage return before the line feed is JSON whitespace, and an
        // empty line is no JSON at all: the contract's reader settles both.
        Command command;
        try
        {
            command = ContractJson.DecodeCommand(line);
        }
        catch (ContractJsonException)
        {
            return "malformed";
        }
        return shell.Execute(command) switch
        {
            ExecuteResult.Accepted => null,
            ExecuteResult.Unsupported => "unsupported",
            ExecuteResult.Full => "full",
            ExecuteResult.Inactive => "inactive",
            ExecuteResult.TooLarge => "too_large",
            _ => "closed",
        };
    }

    private static byte[] Refusal(long number, string reason)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("refused");
            writer.WriteNumber("line", number);
            writer.WriteString("reason", reason);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>The output: whole lines, one writer at a time.</summary>
    private sealed class Lines(Stream output)
    {
        private readonly SemaphoreSlim _turn = new(1, 1);

        internal Task WriteAsync(string member, byte[] json, CancellationToken ended)
        {
            var buffer = new ArrayBufferWriter<byte>(json.Length + 16);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WritePropertyName(member);
                writer.WriteRawValue(json, skipInputValidation: true);
                writer.WriteEndObject();
            }
            return WriteAsync(buffer.WrittenSpan.ToArray(), ended);
        }

        internal async Task WriteAsync(byte[] line, CancellationToken ended)
        {
            await _turn.WaitAsync(ended).ConfigureAwait(false);
            try
            {
                await output.WriteAsync(line, ended).ConfigureAwait(false);
                output.WriteByte(LineFeed);
                await output.FlushAsync(ended).ConfigureAwait(false);
            }
            finally
            {
                _turn.Release();
            }
        }
    }
}
