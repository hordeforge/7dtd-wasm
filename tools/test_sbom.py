#!/usr/bin/env python3
"""Unit tests for tools/sbom.py. Run: python3 -m unittest discover -s tools"""

import json
import pathlib
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

import sbom


def write(path: pathlib.Path, text: str) -> pathlib.Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")
    return path


class NugetComponentsTest(unittest.TestCase):
    def test_dedupes_across_tfms_and_maps_hashes(self):
        lock = write(
            pathlib.Path(tempfile.mkdtemp()) / "packages.lock.json",
            json.dumps({"dependencies": {
                "net8.0": {
                    "Wasmtime": {"type": "Direct", "resolved": "44.0.0",
                                 "contentHash": "abc="},
                },
                ".NETStandard,Version=v2.0": {
                    "Wasmtime": {"type": "Direct", "resolved": "44.0.0",
                                 "contentHash": "abc="},
                    "IndexRange": {"type": "Transitive", "resolved": "1.0.2"},
                    "HordeForge.WasmHost": {"type": "Project", "resolved": None},
                },
            }}),
        )
        comps = sbom.nuget_components(lock)
        self.assertEqual(
            sorted(c["purl"] for c in comps),
            ["pkg:nuget/indexrange@1.0.2", "pkg:nuget/wasmtime@44.0.0"],
        )
        wasmtime = next(c for c in comps if c["name"] == "Wasmtime")
        self.assertEqual(wasmtime["hashes"], [{"alg": "SHA-512", "content": "abc="}])
        indexrange = next(c for c in comps if c["name"] == "IndexRange")
        self.assertNotIn("hashes", indexrange)


class CargoComponentsTest(unittest.TestCase):
    def test_excludes_workspace_members(self):
        tmp = pathlib.Path(tempfile.mkdtemp())
        write(tmp / "Cargo.toml", '[workspace]\nmembers = ["guest-hello"]\n')
        write(tmp / "guest-hello" / "Cargo.toml",
              '[package]\nname = "guest-hello"\nversion = "0.1.0"\n')
        write(tmp / "guest-common" / "Cargo.toml",
              '[package]\nname = "guest-common"\nversion = "0.1.0"\n')
        lock = write(tmp / "Cargo.lock", """
[[package]]
name = "guest-common"
version = "0.1.0"

[[package]]
name = "guest-hello"
version = "0.1.0"

[[package]]
name = "external-crate"
version = "2.1.0"
""")
        purls = [c["purl"] for c in sbom.cargo_components(lock)]
        self.assertEqual(purls, ["pkg:cargo/external-crate@2.1.0"])


class BuildBomTest(unittest.TestCase):
    def test_end_to_end_shape_and_skips_build_output(self):
        root = pathlib.Path(tempfile.mkdtemp())
        write(root / "src" / "GameBridge" / "ModInfo.xml",
              '<xml><Version value="9.9.9" /></xml>')
        write(root / "src" / "GameBridge" / "bin" / "packages.lock.json",
              json.dumps({"dependencies": {"net48": {
                  "Leak": {"type": "Transitive", "resolved": "1.0.0"}}}}))
        write(root / "tests" / "x" / "packages.lock.json",
              json.dumps({"dependencies": {"net8.0": {
                  "xunit": {"type": "Direct", "resolved": "2.9.3"}}}}))
        bom = sbom.build_bom(root)
        self.assertEqual(bom["bomFormat"], "CycloneDX")
        self.assertEqual(bom["specVersion"], sbom.BOM_SPEC_VERSION)
        self.assertEqual(bom["metadata"]["component"]["version"], "9.9.9")
        self.assertEqual([c["purl"] for c in bom["components"]],
                         ["pkg:nuget/xunit@2.9.3"])

    def test_is_deterministic_and_valid_json(self):
        root = pathlib.Path(tempfile.mkdtemp())
        write(root / "src" / "M" / "ModInfo.xml",
              '<xml><Version value="0.1.0" /></xml>')
        write(root / "a" / "packages.lock.json",
              json.dumps({"dependencies": {"net8.0": {
                  "zlib": {"type": "Transitive", "resolved": "1.3"},
                  "alpha": {"type": "Direct", "resolved": "0.2"}}}}))
        one = sbom.build_bom(root)
        two = sbom.build_bom(root)
        self.assertEqual(json.dumps(one, sort_keys=True),
                         json.dumps(two, sort_keys=True))
        self.assertEqual(len(one["components"]), 2)


if __name__ == "__main__":
    unittest.main()
