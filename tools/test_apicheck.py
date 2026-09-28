#!/usr/bin/env python3
"""Unit tests for tools/apicheck.py. Run: python3 -m unittest discover -s tools"""

import contextlib
import io
import pathlib
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

import apicheck

LIBRARY = apicheck.LIBRARY

SOURCE = """\
using System;

namespace HordeForge.WasmHost
{
    /// <summary>A type whose members are all public.</summary>
    public sealed class Sample
    {
        public const int Limit = 4;

        private int hidden;

        public string Name { get; set; }

        public bool TryRead(
            string key,
            out string value)
        {
            value = key;
            return true;
        }

        public void Log(string message) => Console.WriteLine(message);
    }

    public interface IApi
    {
        void Log(string source, int level, string message);
    }
}
"""


def write_library(root: pathlib.Path, source: str = SOURCE) -> pathlib.Path:
    path = root / LIBRARY / "Sample.cs"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(source, encoding="utf-8")
    return path


def run(root: pathlib.Path, *args: str) -> tuple[int, str]:
    stderr = io.StringIO()
    with contextlib.redirect_stderr(stderr):
        code = apicheck.main(["--root", str(root), *args])
    return code, stderr.getvalue()


class SurfaceTest(unittest.TestCase):
    def test_records_public_members_of_every_kind(self):
        root = pathlib.Path(tempfile.mkdtemp())
        write_library(root)
        entries = apicheck.surface(root)
        self.assertIn("src/HordeForge.WasmHost/Sample.cs: type Sample", entries)
        joined = "\n".join(entries)
        self.assertIn("Sample.public const int Limit = 4", joined)
        self.assertIn("Sample.public string Name { get; set; }", joined)
        self.assertIn("Sample.public void Log(string message)", joined)
        self.assertNotIn("hidden", joined)

    def test_joins_a_signature_that_wraps_over_lines(self):
        root = pathlib.Path(tempfile.mkdtemp())
        write_library(root)
        joined = "\n".join(apicheck.surface(root))
        self.assertIn("Sample.public bool TryRead(string key, out string value)", joined)

    def test_interface_member_without_an_access_modifier_is_public(self):
        root = pathlib.Path(tempfile.mkdtemp())
        write_library(root)
        joined = "\n".join(apicheck.surface(root))
        self.assertIn("IApi.public void Log(string source, int level, string message)", joined)

    def test_comments_and_literals_cannot_invent_a_member(self):
        root = pathlib.Path(tempfile.mkdtemp())
        write_library(root, "// public void Ghost();\n/* public void Other(); */\n")
        self.assertEqual(apicheck.surface(root), [])

    def test_internal_type_is_not_on_the_surface(self):
        root = pathlib.Path(tempfile.mkdtemp())
        write_library(root, "namespace N\n{\n    internal sealed class Hidden\n    {\n    }\n}\n")
        self.assertEqual(apicheck.surface(root), [])


class GateTest(unittest.TestCase):
    def test_matching_baseline_passes(self):
        root = pathlib.Path(tempfile.mkdtemp())
        write_library(root)
        self.assertEqual(run(root, "--update")[0], 0)
        self.assertEqual(run(root)[0], 0)

    def test_removed_public_member_fails_and_names_itself(self):
        root = pathlib.Path(tempfile.mkdtemp())
        path = write_library(root)
        run(root, "--update")
        path.write_text(
            SOURCE.replace("public void Log(string message) => Console.WriteLine(message);\n", ""),
            encoding="utf-8",
        )
        code, stderr = run(root)
        self.assertEqual(code, 1)
        self.assertIn("removed", stderr)
        self.assertIn("breaking", stderr)

    def test_changed_signature_fails(self):
        root = pathlib.Path(tempfile.mkdtemp())
        path = write_library(root)
        run(root, "--update")
        path.write_text(SOURCE.replace("out string value", "out string? value"), encoding="utf-8")
        code, stderr = run(root)
        self.assertEqual(code, 1)
        self.assertIn("added", stderr)

    def test_update_writes_a_baseline_the_next_run_accepts(self):
        root = pathlib.Path(tempfile.mkdtemp())
        write_library(root)
        self.assertEqual(run(root, "--update")[0], 0)
        baseline = root / apicheck.BASELINE
        self.assertTrue(baseline.is_file())
        self.assertEqual(run(root)[0], 0)

    def test_missing_baseline_fails_loudly(self):
        root = pathlib.Path(tempfile.mkdtemp())
        write_library(root)
        code, stderr = run(root)
        self.assertEqual(code, 1)
        self.assertIn("--update", stderr)

    def test_missing_library_is_a_usage_error(self):
        root = pathlib.Path(tempfile.mkdtemp())
        self.assertEqual(run(root)[0], 2)


if __name__ == "__main__":
    unittest.main()
