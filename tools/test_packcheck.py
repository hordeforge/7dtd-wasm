#!/usr/bin/env python3
"""Unit tests for tools/packcheck.py. Run: python3 -m unittest discover -s tools"""

import contextlib
import io
import pathlib
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

import packcheck

MANIFEST = """\
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>HordeForge.WasmHost</PackageId>
    <Description>Embeddable WebAssembly mod host.</Description>
    <PackageLicenseExpression>{license}</PackageLicenseExpression>
    <PackageReadmeFile>README.md</PackageReadmeFile>
    <RepositoryUrl>https://github.com/hordeforge/7dtd-wasm</RepositoryUrl>
    <RepositoryType>git</RepositoryType>
    <TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks>
  </PropertyGroup>
  <ItemGroup>
{packed}  </ItemGroup>
</Project>
"""

PACKED = (
    '    <None Include="../../THIRD-PARTY-NOTICES.md" Pack="true" PackagePath="/" />\n'
    '    <None Include="../../LICENSE" Pack="true" PackagePath="/" />\n'
    '    <None Include="../../README.md" Pack="true" PackagePath="/" />\n'
)


def make_repo(manifest: str, license_id: str = "MIT") -> pathlib.Path:
    root = pathlib.Path(tempfile.mkdtemp())
    (root / packcheck.LICENSE).write_text(f"{license_id} License\n\ntext\n", encoding="utf-8")
    csproj = root / packcheck.CSPROJ
    csproj.parent.mkdir(parents=True, exist_ok=True)
    csproj.write_text(manifest, encoding="utf-8")
    return root


def complete_repo(packed: str = PACKED) -> pathlib.Path:
    return make_repo(MANIFEST.format(license="MIT", packed=packed))


class CheckTest(unittest.TestCase):
    def test_complete_manifest_has_no_findings(self):
        self.assertEqual(packcheck.check(complete_repo()), [])

    def test_this_repository_manifest_passes(self):
        self.assertEqual(packcheck.check(packcheck.ROOT), [])

    def test_missing_identity_field_is_reported(self):
        root = make_repo(
            MANIFEST.format(license="MIT", packed=PACKED).replace(
                "<RepositoryUrl>https://github.com/hordeforge/7dtd-wasm</RepositoryUrl>", ""
            )
        )
        self.assertTrue(any("RepositoryUrl" in finding for finding in packcheck.check(root)))

    def test_missing_repository_type_is_reported(self):
        root = make_repo(
            MANIFEST.format(license="MIT", packed=PACKED).replace(
                "<RepositoryType>git</RepositoryType>", ""
            )
        )
        self.assertTrue(any("RepositoryType" in f for f in packcheck.check(root)))

    def test_declared_frameworks_are_read_from_both_spellings(self):
        multi = make_repo(
            MANIFEST.format(license="MIT", packed=PACKED).replace(
                "<TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks>",
                "<TargetFramework>net8.0</TargetFramework>",
            )
        )
        self.assertEqual(packcheck.shipped_frameworks(packcheck.project(multi)), {"net8.0"})

    def test_manifest_without_frameworks_is_reported(self):
        root = make_repo(
            MANIFEST.format(license="MIT", packed=PACKED).replace(
                "<TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks>", ""
            )
        )
        self.assertTrue(
            any("TargetFrameworks" in f for f in packcheck.check(root)),
            "a manifest that names no framework packs nothing",
        )

    def test_a_dropped_framework_is_reported(self):
        root = make_repo(
            MANIFEST.format(license="MIT", packed=PACKED).replace(
                "<TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks>",
                "<TargetFrameworks>net8.0</TargetFrameworks>",
            )
        )
        self.assertTrue(
            any("netstandard2.0" in f for f in packcheck.check(root)),
            "a framework README.md promises must fail the gate",
        )

    def test_license_expression_must_match_the_license_file(self):
        # The manifest claims Apache-2.0 while LICENSE is MIT.
        root = make_repo(MANIFEST.format(license="Apache-2.0", packed=PACKED))
        self.assertTrue(any("PackageLicenseExpression" in f for f in packcheck.check(root)))

    def test_undeclared_readme_is_reported(self):
        root = make_repo(
            MANIFEST.format(license="MIT", packed=PACKED).replace(
                "<PackageReadmeFile>README.md</PackageReadmeFile>", ""
            )
        )
        self.assertTrue(any("PackageReadmeFile" in finding for finding in packcheck.check(root)))

    def test_readme_that_is_not_packed_is_reported(self):
        root = complete_repo(packed="")
        findings = packcheck.check(root)
        self.assertTrue(any("README.md is declared but not packed" in f for f in findings))
        self.assertTrue(any("THIRD-PARTY-NOTICES.md is not packed" in f for f in findings))
        self.assertTrue(any("LICENSE is not packed" in f for f in findings))

    def test_license_missing_while_notices_ship_is_reported(self):
        root = complete_repo(
            packed=PACKED.replace(
                '    <None Include="../../LICENSE" Pack="true" PackagePath="/" />\n', ""
            )
        )
        findings = packcheck.check(root)
        self.assertTrue(
            any("LICENSE is not packed" in f for f in findings),
            "the notices link to LICENSE, so a package without it ships a dead reference",
        )
        self.assertFalse(any("THIRD-PARTY-NOTICES.md is not packed" in f for f in findings))

    def test_a_file_without_pack_true_is_not_a_packed_file(self):
        root = complete_repo(
            packed='    <None Include="../../README.md" />\n'
            '    <None Include="../../THIRD-PARTY-NOTICES.md" Pack="true" />\n'
            '    <None Include="../../LICENSE" Pack="true" />\n'
        )
        findings = packcheck.check(root)
        self.assertTrue(any("README.md is declared but not packed" in f for f in findings))
        self.assertFalse(any("THIRD-PARTY-NOTICES.md" in f for f in findings))
        self.assertFalse(any("LICENSE is not packed" in f for f in findings))

    def test_license_id_reads_the_spdx_id_from_the_license_header(self):
        self.assertEqual(packcheck.license_id(packcheck.ROOT), "MIT")


class MainTest(unittest.TestCase):
    """The command-line contract: exit codes and the stdout/stderr split."""

    def run_main(self, root):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = packcheck.main(["--root", str(root)])
        return code, out.getvalue(), err.getvalue()

    def test_complete_manifest_exits_zero_with_empty_stdout(self):
        code, out, err = self.run_main(complete_repo())
        self.assertEqual(code, 0)
        self.assertEqual(out, "")
        self.assertIn("packcheck: ok", err)

    def test_findings_exit_one_on_stderr(self):
        root = make_repo(MANIFEST.format(license="Apache-2.0", packed=PACKED))
        code, out, err = self.run_main(root)
        self.assertEqual(code, 1)
        self.assertEqual(out, "")
        self.assertIn("PackageLicenseExpression", err)

    def test_missing_root_is_a_usage_error(self):
        with tempfile.TemporaryDirectory() as tmp:
            code, out, err = self.run_main(pathlib.Path(tmp) / "absent")
        self.assertEqual(code, 2)
        self.assertEqual(out, "")
        self.assertIn("is not a directory", err)


if __name__ == "__main__":
    unittest.main()
