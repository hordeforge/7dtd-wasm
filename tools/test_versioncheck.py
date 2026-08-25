#!/usr/bin/env python3
"""Unit tests for tools/versioncheck.py. Run: python3 -m unittest discover -s tools"""

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
        modinfo = write(root / "ModInfo.xml",
                        '<xml>\n  <Name value="m" />\n'
                        '  <Version value="0.1.5" />\n</xml>\n')
        self.assertEqual(versioncheck.read_version(modinfo), "0.1.5")

    def test_missing_modinfo_version_raises(self):
        root = pathlib.Path(tempfile.mkdtemp())
        modinfo = write(root / "ModInfo.xml", "<xml></xml>\n")
        with self.assertRaises(ValueError):
            versioncheck.read_version(modinfo)

    def test_reads_csproj_version_not_package_reference(self):
        root = pathlib.Path(tempfile.mkdtemp())
        csproj = write(root / "lib.csproj",
                       '<Project>\n  <LangVersion>latest</LangVersion>\n'
                       '  <Version>1.2.3</Version>\n  <PackageReference\n'
                       '    Include="Wasmtime" Version="44.0.0" />\n'
                       '</Project>\n')
        self.assertEqual(versioncheck.package_version(csproj), "1.2.3")

    def test_missing_csproj_version_raises(self):
        root = pathlib.Path(tempfile.mkdtemp())
        csproj = write(root / "lib.csproj", "<Project></Project>\n")
        with self.assertRaises(ValueError):
            versioncheck.package_version(csproj)


class ReleasedVersionTest(unittest.TestCase):
    def test_newest_released_section_wins_and_skips_unreleased(self):
        root = pathlib.Path(tempfile.mkdtemp())
        changelog = write(root / "CHANGELOG.md",
                          "# Changelog\n\n## Unreleased\n\n### Added\n\n"
                          "- pending\n\n## [0.2.0] - 2026-08-25\n\n- two\n\n"
                          "## [0.1.5] - 2026-08-24\n\n- one\n")
        self.assertEqual(versioncheck.released_version(changelog), "0.2.0")

    def test_changelog_without_release_section_raises(self):
        root = pathlib.Path(tempfile.mkdtemp())
        changelog = write(root / "CHANGELOG.md", "# Changelog\n\n## Unreleased\n")
        with self.assertRaises(ValueError):
            versioncheck.released_version(changelog)


if __name__ == "__main__":
    unittest.main()
