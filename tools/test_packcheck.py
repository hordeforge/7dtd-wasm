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
  </PropertyGroup>
  <ItemGroup>
{packed}  </ItemGroup>
</Project>
"""

PACKED = (
    '    <None Include="../../THIRD-PARTY-NOTICES.md" Pack="true" PackagePath="/" />\n'
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

    def test_a_file_without_pack_true_is_not_a_packed_file(self):
        root = complete_repo(
            packed='    <None Include="../../README.md" />\n'
            '    <None Include="../../THIRD-PARTY-NOTICES.md" Pack="true" />\n'
        )
        findings = packcheck.check(root)
        self.assertTrue(any("README.md is declared but not packed" in f for f in findings))
        self.assertFalse(any("THIRD-PARTY-NOTICES.md" in f for f in findings))

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
