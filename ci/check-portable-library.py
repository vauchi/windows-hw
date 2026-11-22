#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
# SPDX-License-Identifier: GPL-3.0-or-later
"""The portable library stays portable and depends on the contract alone.

`src/Vauchi.WindowsHw` holds the ports, the sessions and the shell. It
runs wherever .NET does, which is what lets the fixtures replay in a
Linux container (ADR-077 D-7), and it names no display toolkit, no Core
assembly and no Windows API (ADR-077 items 1 and 6; ADR-078: a hardware
shell depends on the contract). This fails when a project file or a
source file there says otherwise.

    check-portable-library.py [DIRECTORY]
"""

import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ALLOWED_PACKAGES = {"Vauchi.Contract"}
# A `using` or a fully qualified name of a display toolkit, a Windows API
# projection or Core's C ABI.
FORBIDDEN_NAMESPACES = re.compile(
    r"\b(?:Windows\.(?:Devices|Media|UI|Storage|Networking|Foundation|ApplicationModel|System)"
    r"|Microsoft\.(?:UI|Windows)|WinRT|Vauchi\.Interop|vauchi_cabi)\b"
)


def project_violations(path):
    root = ET.parse(path).getroot()
    for framework in root.iter("TargetFramework"):
        if (framework.text or "").strip() != "net8.0":
            yield f"{path}: TargetFramework {framework.text!r} is not net8.0"
    for frameworks in root.iter("TargetFrameworks"):
        yield f"{path}: TargetFrameworks {frameworks.text!r}: one portable target only"
    for reference in root.iter("PackageReference"):
        name = reference.get("Include") or reference.get("Update") or ""
        if name not in ALLOWED_PACKAGES:
            yield f"{path}: package {name!r} is not the contract"
    for reference in root.iter("ProjectReference"):
        yield f"{path}: project reference {reference.get('Include')!r}"
    for reference in root.iter("Reference"):
        yield f"{path}: assembly reference {reference.get('Include')!r}"
    for reference in root.iter("FrameworkReference"):
        yield f"{path}: framework reference {reference.get('Include')!r}"
    for flag in ("UseWinUI", "UseWPF", "UseWindowsForms"):
        for element in root.iter(flag):
            yield f"{path}: {flag} is set"


def source_violations(path):
    for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        found = FORBIDDEN_NAMESPACES.search(line)
        if found:
            yield f"{path}:{number}: names {found.group(0)}"


def violations(directory):
    projects = sorted(directory.glob("*.csproj"))
    sources = sorted(path for path in directory.rglob("*.cs") if not {"bin", "obj"} & set(path.parts))
    if not projects or not sources:
        # An empty or misplaced directory must not read as a clean one.
        yield f"{directory}: no project or no source file to check"
        return
    for project in projects:
        yield from project_violations(project)
    for source in sources:
        yield from source_violations(source)


def main(arguments):
    directory = Path(arguments[0]) if arguments else Path(__file__).resolve().parents[1] / "src/Vauchi.WindowsHw"
    found = list(violations(directory))
    for violation in found:
        print(violation, file=sys.stderr)
    if found:
        print(f"{len(found)} violation(s): the portable library must stay portable", file=sys.stderr)
        return 1
    print(f"{directory}: portable, and the contract is its only dependency")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
