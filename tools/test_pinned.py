#!/usr/bin/env python3
"""Unit tests for tools/pinned.py. Run: python3 -m unittest discover -s tools"""

import contextlib
import io
import json
import pathlib
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

import pinned


def repo(tmp: pathlib.Path) -> pathlib.Path:
    """A minimal tree carrying the three declaring files."""
    (tmp / "src" / "HordeForge.WasmHost").mkdir(parents=True)
    (tmp / "samples").mkdir()
    (tmp / "pyproject.toml").write_text(
        '[tool.ruff]\nrequired-version = "==0.16.4"\n', encoding="utf-8"
    )
    (tmp / "samples" / "rust-toolchain.toml").write_text(
        '[toolchain]\nchannel = "1.97.1"\n', encoding="utf-8"
    )
    (tmp / pinned.WASMTIME_LOCK).write_text(
        json.dumps(
            {
                "dependencies": {
                    "net8.0": {"Wasmtime": {"type": "Direct", "resolved": "44.0.0"}},
                    "netstandard2.0": {"Wasmtime": {"type": "Direct", "resolved": "44.0.0"}},
                }
            }
        ),
        encoding="utf-8",
    )
    return tmp


class PinsTest(unittest.TestCase):
    def test_reads_each_pin_from_its_declaring_file(self):
        root = repo(pathlib.Path(tempfile.mkdtemp()))
        self.assertEqual(pinned.ruff_version(root), "0.16.4")
        self.assertEqual(pinned.wasmtime_version(root), "44.0.0")
        self.assertEqual(pinned.rust_version(root), "1.97.1")

    def test_this_repository_declares_every_pin(self):
        # The Makefile substitutes these into the preflight and the dist
        # staging, so a pin the repo no longer declares has to fail here.
        for name, read in pinned.PINS.items():
            with self.subTest(pin=name):
                self.assertRegex(read(pinned.ROOT), r"\S")

    def test_ambiguous_wasmtime_pin_fails(self):
        root = repo(pathlib.Path(tempfile.mkdtemp()))
        (root / pinned.WASMTIME_LOCK).write_text(
            json.dumps(
                {
                    "dependencies": {
                        "net8.0": {"Wasmtime": {"resolved": "44.0.0"}},
                        "netstandard2.0": {"Wasmtime": {"resolved": "45.0.0"}},
                    }
                }
            ),
            encoding="utf-8",
        )
        with self.assertRaises(pinned.PinError):
            pinned.wasmtime_version(root)

    def test_missing_wasmtime_pin_fails(self):
        root = repo(pathlib.Path(tempfile.mkdtemp()))
        (root / pinned.WASMTIME_LOCK).write_text(
            json.dumps({"dependencies": {"net8.0": {"IndexRange": {"resolved": "1.0.2"}}}}),
            encoding="utf-8",
        )
        with self.assertRaises(pinned.PinError):
            pinned.wasmtime_version(root)

    def test_malformed_and_absent_files_fail(self):
        root = repo(pathlib.Path(tempfile.mkdtemp()))
        (root / pinned.WASMTIME_LOCK).write_text("{", encoding="utf-8")
        with self.assertRaises(pinned.PinError):
            pinned.wasmtime_version(root)
        (root / pinned.RUST_TOOLCHAIN).unlink()
        with self.assertRaises(pinned.PinError):
            pinned.rust_version(root)

    def test_empty_declaration_fails(self):
        root = repo(pathlib.Path(tempfile.mkdtemp()))
        (root / "pyproject.toml").write_text("[tool.ruff]\n", encoding="utf-8")
        (root / pinned.RUST_TOOLCHAIN).write_text("[toolchain]\n", encoding="utf-8")
        with self.assertRaises(pinned.PinError):
            pinned.ruff_version(root)
        with self.assertRaises(pinned.PinError):
            pinned.rust_version(root)

    def test_cli_prints_only_the_version(self):
        root = repo(pathlib.Path(tempfile.mkdtemp()))
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(pinned.main(["wasmtime", "--root", str(root)]), 0)
        self.assertEqual(out.getvalue(), "44.0.0\n")

    def test_cli_reports_a_missing_root_as_a_usage_error(self):
        # 2, not 1: a --root that is not a directory says nothing about the
        # declarations, the same split the other tools make.
        missing = pathlib.Path(tempfile.mkdtemp()) / "nope"
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            self.assertEqual(pinned.main(["wasmtime", "--root", str(missing)]), 2)
        self.assertEqual(out.getvalue(), "")
        self.assertIn("is not a directory", err.getvalue())


if __name__ == "__main__":
    unittest.main()
