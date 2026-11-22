<!-- SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me> -->
<!-- SPDX-License-Identifier: GPL-3.0-or-later -->

# windows-hw

Vauchi's hardware shell for Windows. It executes the hardware commands
[Vauchi Core](https://gitlab.com/vauchi/core) emits and reports what the
OS observed as events, through WinRT Bluetooth LE, WinSCard,
MediaCapture and DNS-SD. It decides nothing: Core chooses peers, roles,
framing, deadlines and fallback. It shows no UI: the app's display shell
renders everything the user sees.

It serves the Windows app.

## Contents

- `src/Vauchi.WindowsHw` (`net8.0`): the ports as interfaces, one session
  per port and `HardwareShell`, which takes Core's hardware commands and
  hands back the events the backends report. It names no Windows API, so
  it builds and is tested in a Linux container. Direct send over TCP
  lives here.
- `HostLoop`, in the same library: the conversation of the helper
  process a terminal starts, one contract `Command` per line in and one
  line per observation or refusal out, over inherited pipes.
- `tests/Vauchi.WindowsHw.Tests`: every command and event against
  scripted backends, including the hardware commands and events of the
  contract's shared fixture (`contract:replay` in CI).
- `ci/check-portable-library.py`: fails when the portable library names
  a Windows API, a display toolkit or a Core assembly.

- `src/Vauchi.WindowsHw.Native` (Windows only): the backends behind the
  ports. So far the capability probe and discovery on WinRT.
- `src/vauchi-hw-host`: the helper program. Published ahead of time it is
  one native file; `tests/Vauchi.WindowsHw.Host.Tests` starts it as a
  child process, and with `VAUCHI_HW_HOST` set, the published file.
- `tools/Probe`: prints the capability report, scans for a service UUID
  with the time of each observation, and with `survey` lists the service
  UUIDs advertised nearby.

Still to come: Bluetooth LE links in both roles, WinSCard, MediaCapture,
packaging and signing of the helper, and the remaining measurement
tools.

## Build and test

```sh
dotnet test tests/Vauchi.WindowsHw.Tests/Vauchi.WindowsHw.Tests.csproj
python3 ci/tests/check-portable-library.test.py
python3 ci/check-portable-library.py
```

On Windows, also:

```sh
dotnet test tests/Vauchi.WindowsHw.Host.Tests/Vauchi.WindowsHw.Host.Tests.csproj
dotnet run --project tools/Probe/Probe.csproj -- survey 5
```

The types come from the contract's C# library, `Vauchi.Contract`, pinned
exactly in `Directory.Build.props` and restored from the contract
project's package registry (`nuget.config`).

## License

GPL-3.0-or-later; see `LICENSE`.
