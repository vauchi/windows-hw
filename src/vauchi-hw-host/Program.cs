// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

// The helper process: this machine's hardware shell behind the line
// protocol of HostLoop, on the standard input and output the terminal
// that started it handed over. It takes no argument and opens nothing
// by name. It ends when its input ends, which is also what a terminal
// that died looks like.

using System;
using System.IO;
using System.Threading;
using Vauchi.Contract;
using Vauchi.WindowsHw;
using Vauchi.WindowsHw.Native;

DeviceCapabilities device = await WindowsProbe.ProbeAsync();
var backends = new Backends
{
    Discovery = new WinRtDiscovery(),
    Nfc = new PcscReader(new WinSCard()),
    Direct = new TcpDirectExchange(),
};
await using var shell = new HardwareShell(backends, device);
using Stream input = Console.OpenStandardInput();
using Stream output = Console.OpenStandardOutput();
await HostLoop.RunAsync(shell, input, output, CancellationToken.None);
