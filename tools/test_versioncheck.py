#!/usr/bin/env python3
"""Unit tests for tools/versioncheck.py. Run: python3 -m unittest discover -s tools"""

import contextlib
import io
import pathlib
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

import versioncheck


def write(path: pathlib.Path, text: str) -> pathlib.Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")
    return path


class ReadVersionTest(unittest.TestCase):
    def test_reads_modinfo_version(self):
        root = pathlib.Path(tempfile.mkdtemp())
        modinfo = write(
            root / "ModInfo.xml",
            '<xml>\n  <Name value="m" />\n  <Version value="0.1.5" />\n</xml>\n',
        )
        self.assertEqual(versioncheck.read_version(modinfo), "0.1.5")

    def test_missing_modinfo_version_raises(self):
        root = pathlib.Path(tempfile.mkdtemp())
        modinfo = write(root / "ModInfo.xml", "<xml></xml>\n")
        with self.assertRaises(ValueError):
            versioncheck.read_version(modinfo)

    def test_reads_csproj_version_not_package_reference(self):
        root = pathlib.Path(tempfile.mkdtemp())
        csproj = write(
            root / "lib.csproj",
            "<Project>\n  <LangVersion>latest</LangVersion>\n"
            "  <Version>1.2.3</Version>\n  <PackageReference\n"
            '    Include="Wasmtime" Version="44.0.0" />\n'
            "</Project>\n",
        )
        self.assertEqual(versioncheck.package_version(csproj), "1.2.3")

    def test_missing_csproj_version_raises(self):
        root = pathlib.Path(tempfile.mkdtemp())
        csproj = write(root / "lib.csproj", "<Project></Project>\n")
        with self.assertRaises(ValueError):
            versioncheck.package_version(csproj)


class ReleasedVersionTest(unittest.TestCase):
    def test_newest_released_section_wins_and_skips_unreleased(self):
        root = pathlib.Path(tempfile.mkdtemp())
        changelog = write(
            root / "CHANGELOG.md",
            "# Changelog\n\n## Unreleased\n\n### Added\n\n"
            "- pending\n\n## [0.2.0] - 2026-08-25\n\n- two\n\n"
            "## [0.1.5] - 2026-08-24\n\n- one\n",
        )
        self.assertEqual(versioncheck.released_version(changelog), "0.2.0")

    def test_changelog_without_release_section_raises(self):
        root = pathlib.Path(tempfile.mkdtemp())
        changelog = write(root / "CHANGELOG.md", "# Changelog\n\n## Unreleased\n")
        with self.assertRaises(ValueError):
            versioncheck.released_version(changelog)


class MainTest(unittest.TestCase):
    """The gate reports on stderr and leaves stdout empty for the caller."""

    def write_repo(self, root, version="1.2.3"):
        (root / "src" / "GameBridge").mkdir(parents=True)
        (root / "src" / "HordeForge.WasmHost").mkdir(parents=True)
        (root / "src" / "GameBridge" / "ModInfo.xml").write_text(
            f'<xml><Version value="{version}" /></xml>\n', encoding="utf-8"
        )
        (root / "src" / "HordeForge.WasmHost" / "HordeForge.WasmHost.csproj").write_text(
            f"<Project><Version>{version}</Version></Project>\n", encoding="utf-8"
        )
        (root / "CHANGELOG.md").write_text(
            f"# Changelog\n\n## [{version}] - 2026-08-25\n\n- one\n", encoding="utf-8"
        )

    def run_main(self, root, extra=()):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = versioncheck.main(["--root", str(root), *extra])
        return code, out.getvalue(), err.getvalue()

    def test_agreeing_versions_exit_zero_with_empty_stdout(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            self.write_repo(root)
            code, out, err = self.run_main(root)
        self.assertEqual(code, 0)
        self.assertEqual(out, "")
        self.assertIn("versioncheck: ok", err)

    def test_disagreeing_versions_exit_one_on_stderr(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            self.write_repo(root, version="1.2.3")
            (root / "CHANGELOG.md").write_text(
                "# Changelog\n\n## [9.9.9] - 2026-08-25\n\n- one\n", encoding="utf-8"
            )
            code, out, err = self.run_main(root)
        self.assertEqual(code, 1)
        self.assertEqual(out, "")
        self.assertIn("disagree", err)

    def test_missing_repository_exits_one_on_stderr(self):
        with tempfile.TemporaryDirectory() as tmp:
            code, out, err = self.run_main(pathlib.Path(tmp) / "absent")
        self.assertEqual(code, 1)
        self.assertEqual(out, "")
        self.assertIn("versioncheck:", err)


class TagTest(unittest.TestCase):
    """--tag is the release workflow's gate: the tag names the version."""

    def setUp(self):
        self.root = pathlib.Path(tempfile.mkdtemp())
        (self.root / "src" / "GameBridge").mkdir(parents=True)
        (self.root / "src" / "HordeForge.WasmHost").mkdir(parents=True)
        (self.root / "src" / "GameBridge" / "ModInfo.xml").write_text(
            '<xml><Version value="1.2.3" /></xml>\n', encoding="utf-8"
        )
        (self.root / "src" / "HordeForge.WasmHost" / "HordeForge.WasmHost.csproj").write_text(
            "<Project><Version>1.2.3</Version></Project>\n", encoding="utf-8"
        )
        (self.root / "CHANGELOG.md").write_text(
            "# Changelog\n\n## [1.2.3] - 2026-08-25\n\n- one\n", encoding="utf-8"
        )

    def run_main(self, tag):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = versioncheck.main(["--root", str(self.root), "--tag", tag])
        return code, out.getvalue(), err.getvalue()

    def test_matching_tag_exits_zero(self):
        code, out, err = self.run_main("v1.2.3")
        self.assertEqual(code, 0)
        self.assertEqual(out, "")
        self.assertIn("ok (v1.2.3)", err)

    def test_tag_above_the_shipped_version_names_every_mismatch(self):
        code, out, err = self.run_main("v1.3.0")
        self.assertEqual(code, 1)
        self.assertEqual(out, "")
        self.assertIn("tag v1.3.0 but CHANGELOG.md ships 1.2.3", err)
        self.assertIn("tag v1.3.0 but src/GameBridge/ModInfo.xml ships 1.2.3", err)
        self.assertIn("make them match before tagging", err)

    def test_tag_without_the_v_prefix_is_rejected(self):
        code, _, err = self.run_main("1.2.3")
        self.assertEqual(code, 1)
        self.assertIn("does not start with", err)

    def test_disagreeing_declarations_fail_before_the_tag_is_compared(self):
        (self.root / "CHANGELOG.md").write_text(
            "# Changelog\n\n## [9.9.9] - 2026-08-25\n\n- one\n", encoding="utf-8"
        )
        code, _, err = self.run_main("v1.2.3")
        self.assertEqual(code, 1)
        self.assertIn("disagree", err)


if __name__ == "__main__":
    unittest.main()
