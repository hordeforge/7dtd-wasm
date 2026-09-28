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
            json.dumps(
                {
                    "dependencies": {
                        "net8.0": {
                            "Wasmtime": {
                                "type": "Direct",
                                "resolved": "44.0.0",
                                "contentHash": "abc=",
                            },
                        },
                        ".NETStandard,Version=v2.0": {
                            "Wasmtime": {
                                "type": "Direct",
                                "resolved": "44.0.0",
                                "contentHash": "abc=",
                            },
                            "IndexRange": {"type": "Transitive", "resolved": "1.0.2"},
                            "HordeForge.WasmHost": {"type": "Project", "resolved": None},
                        },
                    }
                }
            ),
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

    def test_every_component_carries_its_spdx_license(self):
        lock = write(
            pathlib.Path(tempfile.mkdtemp()) / "packages.lock.json",
            json.dumps(
                {
                    "dependencies": {
                        "net8.0": {
                            "Wasmtime": {"type": "Direct", "resolved": "44.0.0"},
                            "xunit": {"type": "Direct", "resolved": "2.9.3"},
                        }
                    }
                }
            ),
        )
        licenses = {c["name"]: c["licenses"] for c in sbom.nuget_components(lock)}
        self.assertEqual(
            licenses["Wasmtime"],
            [{"license": {"id": "Apache-2.0 WITH LLVM-exception"}}],
        )
        self.assertEqual(licenses["xunit"], [{"license": {"id": "Apache-2.0"}}])

    def test_unrecorded_license_fails_loudly(self):
        lock = write(
            pathlib.Path(tempfile.mkdtemp()) / "packages.lock.json",
            json.dumps(
                {
                    "dependencies": {
                        "net8.0": {
                            "Newtonsoft.Json": {"type": "Transitive", "resolved": "13.0.3"},
                        }
                    }
                }
            ),
        )
        with self.assertRaises(SystemExit) as caught:
            sbom.nuget_components(lock)
        self.assertIn("Newtonsoft.Json", str(caught.exception))


class NoticesTest(unittest.TestCase):
    """THIRD-PARTY-NOTICES.md must cover every package the SBOM can emit."""

    NOTICES = pathlib.Path(__file__).resolve().parent.parent / "THIRD-PARTY-NOTICES.md"

    def test_every_recorded_license_is_named_in_the_notices(self):
        text = self.NOTICES.read_text(encoding="utf-8").lower()
        for name, license_id in sbom.NUGET_LICENSES.items():
            with self.subTest(package=name):
                self.assertIn(name, text)
                self.assertIn(license_id.split(" WITH ")[0].lower(), text)


class CargoComponentsTest(unittest.TestCase):
    def test_excludes_workspace_members(self):
        tmp = pathlib.Path(tempfile.mkdtemp())
        write(tmp / "Cargo.toml", '[workspace]\nmembers = ["guest-hello"]\n')
        write(
            tmp / "guest-hello" / "Cargo.toml",
            '[package]\nname = "guest-hello"\nversion = "0.1.0"\n',
        )
        write(
            tmp / "guest-common" / "Cargo.toml",
            '[package]\nname = "guest-common"\nversion = "0.1.0"\n',
        )
        lock = write(
            tmp / "Cargo.lock",
            """
[[package]]
name = "guest-common"
version = "0.1.0"

[[package]]
name = "guest-hello"
version = "0.1.0"

[[package]]
name = "external-crate"
version = "2.1.0"
""",
        )
        purls = [c["purl"] for c in sbom.cargo_components(lock)]
        self.assertEqual(purls, ["pkg:cargo/external-crate@2.1.0"])


class BuildBomTest(unittest.TestCase):
    def test_end_to_end_shape_and_skips_build_output(self):
        root = pathlib.Path(tempfile.mkdtemp())
        write(root / "src" / "GameBridge" / "ModInfo.xml", '<xml><Version value="9.9.9" /></xml>')
        write(
            root / "src" / "GameBridge" / "bin" / "packages.lock.json",
            json.dumps(
                {
                    "dependencies": {
                        "net48": {"IndexRange": {"type": "Transitive", "resolved": "1.0.0"}}
                    }
                }
            ),
        )
        write(
            root / "tests" / "x" / "packages.lock.json",
            json.dumps(
                {"dependencies": {"net8.0": {"xunit": {"type": "Direct", "resolved": "2.9.3"}}}}
            ),
        )
        bom = sbom.build_bom(root)
        self.assertEqual(bom["bomFormat"], "CycloneDX")
        self.assertEqual(bom["specVersion"], sbom.BOM_SPEC_VERSION)
        self.assertEqual(bom["metadata"]["component"]["version"], "9.9.9")
        self.assertEqual([c["purl"] for c in bom["components"]], ["pkg:nuget/xunit@2.9.3"])

    def test_evidence_locks_are_not_inventory(self):
        """evidence/ is a frozen playtest record, not a shipped artifact."""
        root = pathlib.Path(tempfile.mkdtemp())
        write(root / "src" / "M" / "ModInfo.xml", '<xml><Version value="0.1.0" /></xml>')
        write(
            root / "src" / "M" / "packages.lock.json",
            json.dumps(
                {"dependencies": {"net8.0": {"Wasmtime": {"type": "Direct", "resolved": "44.0.0"}}}}
            ),
        )
        write(
            root / "evidence" / "playtest-1" / "client" / "packages.lock.json",
            json.dumps(
                {"dependencies": {"net8.0": {"xunit": {"type": "Direct", "resolved": "2.9.3"}}}}
            ),
        )
        self.assertEqual(
            [c["purl"] for c in sbom.build_bom(root)["components"]], ["pkg:nuget/wasmtime@44.0.0"]
        )

    def test_is_deterministic_and_valid_json(self):
        root = pathlib.Path(tempfile.mkdtemp())
        write(root / "src" / "M" / "ModInfo.xml", '<xml><Version value="0.1.0" /></xml>')
        write(
            root / "a" / "packages.lock.json",
            json.dumps(
                {
                    "dependencies": {
                        "net8.0": {
                            "System.Buffers": {"type": "Transitive", "resolved": "4.5.1"},
                            "Wasmtime": {"type": "Direct", "resolved": "44.0.0"},
                        }
                    }
                }
            ),
        )
        one = sbom.build_bom(root)
        two = sbom.build_bom(root)
        self.assertEqual(json.dumps(one, sort_keys=True), json.dumps(two, sort_keys=True))
        self.assertEqual(len(one["components"]), 2)


class MalformedInputTest(unittest.TestCase):
    """A broken lock file must name itself, not raise a bare traceback."""

    def test_malformed_nuget_lock_names_the_file(self):
        lock = write(pathlib.Path(tempfile.mkdtemp()) / "packages.lock.json", "{ not json")
        with self.assertRaises(SystemExit) as caught:
            sbom.nuget_components(lock)
        self.assertIn(str(lock), str(caught.exception))

    def test_malformed_cargo_lock_names_the_file(self):
        lock = write(pathlib.Path(tempfile.mkdtemp()) / "Cargo.lock", "= broken =")
        with self.assertRaises(SystemExit) as caught:
            sbom.cargo_components(lock)
        self.assertIn(str(lock), str(caught.exception))

    def test_malformed_modinfo_names_the_file(self):
        root = pathlib.Path(tempfile.mkdtemp())
        modinfo = write(root / "src" / "M" / "ModInfo.xml", "<Mod><Version>")
        with self.assertRaises(SystemExit) as caught:
            sbom.project_version(root)
        self.assertIn(str(modinfo), str(caught.exception))


class MainTest(unittest.TestCase):
    def test_missing_root_fails_with_a_message_not_a_traceback(self):
        missing = pathlib.Path(tempfile.mkdtemp()) / "nope"
        self.assertEqual(sbom.main(["--root", str(missing)]), 2)

    def test_unwritable_output_fails_instead_of_raising(self):
        root = pathlib.Path(tempfile.mkdtemp())
        write(root / "src" / "M" / "ModInfo.xml", '<xml><Version value="1.0.0" /></xml>')
        blocker = root / "blocker"
        blocker.write_text("not a directory", encoding="utf-8")
        self.assertEqual(
            sbom.main(["--root", str(root), "-o", str(blocker / "sub" / "out.json")]), 2
        )


if __name__ == "__main__":
    unittest.main()
