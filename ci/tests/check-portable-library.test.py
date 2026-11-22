#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
# SPDX-License-Identifier: GPL-3.0-or-later
"""The portability check passes on a clean library and fails on each planted violation."""

import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CHECK = ROOT / "ci/check-portable-library.py"

CLEAN_PROJECT = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Vauchi.Contract" Version="[0.100.0]" />
  </ItemGroup>
</Project>
"""
CLEAN_SOURCE = "using System;\nusing Vauchi.Contract;\n\nnamespace Vauchi.WindowsHw;\n"


class PortableLibraryCheckTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.library = Path(self.temporary.name) / "Vauchi.WindowsHw"
        self.library.mkdir()
        self.project = self.library / "Vauchi.WindowsHw.csproj"
        self.source = self.library / "Shell.cs"
        self.project.write_text(CLEAN_PROJECT, encoding="utf-8")
        self.source.write_text(CLEAN_SOURCE, encoding="utf-8")

    def tearDown(self):
        self.temporary.cleanup()

    def check(self, directory=None):
        return subprocess.run(
            [sys.executable, str(CHECK), str(directory or self.library)],
            capture_output=True,
            text=True,
            check=False,
            timeout=60,
        )

    def assert_refused(self, expected):
        result = self.check()
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertIn(expected, result.stderr)

    def test_the_repositorys_library_is_portable(self):
        result = subprocess.run([sys.executable, str(CHECK)], capture_output=True, text=True, check=False, timeout=60)

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("portable, and the contract is its only dependency", result.stdout)

    def test_a_clean_library_passes(self):
        result = self.check()

        self.assertEqual(0, result.returncode, result.stderr)

    def test_a_windows_target_is_refused(self):
        self.project.write_text(
            CLEAN_PROJECT.replace("net8.0", "net8.0-windows10.0.19041.0"), encoding="utf-8"
        )

        self.assert_refused("TargetFramework 'net8.0-windows10.0.19041.0' is not net8.0")

    def test_a_second_target_is_refused(self):
        self.project.write_text(
            CLEAN_PROJECT.replace(
                "<TargetFramework>net8.0</TargetFramework>",
                "<TargetFrameworks>net8.0;net8.0-windows10.0.19041.0</TargetFrameworks>",
            ),
            encoding="utf-8",
        )

        self.assert_refused("one portable target only")

    def test_another_package_is_refused(self):
        for package in ("Microsoft.WindowsAppSDK", "ZXing.Net", "Vauchi.Core"):
            with self.subTest(package=package):
                self.project.write_text(
                    CLEAN_PROJECT.replace(
                        "</ItemGroup>", f'  <PackageReference Include="{package}" Version="1.0.0" />\n  </ItemGroup>'
                    ),
                    encoding="utf-8",
                )

                self.assert_refused(f"package '{package}' is not the contract")

    def test_a_project_reference_is_refused(self):
        self.project.write_text(
            CLEAN_PROJECT.replace(
                "</ItemGroup>", '  <ProjectReference Include="..\\\\Native\\\\Native.csproj" />\n  </ItemGroup>'
            ),
            encoding="utf-8",
        )

        self.assert_refused("project reference")

    def test_a_display_toolkit_switch_is_refused(self):
        self.project.write_text(
            CLEAN_PROJECT.replace("</PropertyGroup>", "  <UseWinUI>true</UseWinUI>\n  </PropertyGroup>"),
            encoding="utf-8",
        )

        self.assert_refused("UseWinUI is set")

    def test_a_forbidden_namespace_is_refused_with_its_line(self):
        for namespace, reported in (
            ("Windows.Devices.Bluetooth", "Windows.Devices"),
            ("Windows.Media.Capture", "Windows.Media"),
            ("Microsoft.UI.Xaml", "Microsoft.UI"),
            ("Microsoft.Windows.AppNotifications", "Microsoft.Windows"),
            ("WinRT", "WinRT"),
            ("Vauchi.Interop", "Vauchi.Interop"),
        ):
            with self.subTest(namespace=namespace):
                self.source.write_text(CLEAN_SOURCE + f"using {namespace};\n", encoding="utf-8")

                self.assert_refused(f"Shell.cs:5: names {reported}\n")

    def test_a_fully_qualified_use_is_refused(self):
        self.source.write_text(
            CLEAN_SOURCE + "class X { object Y = new Windows.Devices.Bluetooth.BluetoothLEDevice(); }\n",
            encoding="utf-8",
        )

        self.assert_refused("names Windows.Devices")

    def test_words_that_only_resemble_a_namespace_pass(self):
        self.source.write_text(
            CLEAN_SOURCE + "// windows-hw reads no Windows registry; SuperWinRTLike and MyWindows.Devices are names\n",
            encoding="utf-8",
        )

        result = self.check()

        self.assertEqual(0, result.returncode, result.stderr)

    def test_build_output_is_not_read(self):
        generated = self.library / "obj"
        generated.mkdir()
        (generated / "Generated.cs").write_text("using Windows.Devices.Bluetooth;\n", encoding="utf-8")

        result = self.check()

        self.assertEqual(0, result.returncode, result.stderr)

    def test_an_empty_directory_is_not_a_clean_one(self):
        empty = Path(self.temporary.name) / "empty"
        empty.mkdir()

        result = self.check(empty)

        self.assertEqual(1, result.returncode, result.stdout)
        self.assertIn("no project or no source file to check", result.stderr)

    def test_a_missing_directory_is_not_a_clean_one(self):
        result = self.check(Path(self.temporary.name) / "renamed-away")

        self.assertEqual(1, result.returncode, result.stdout)


if __name__ == "__main__":
    unittest.main()
