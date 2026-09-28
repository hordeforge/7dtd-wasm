#!/usr/bin/env python3
"""Generate a CycloneDX SBOM from the committed dependency lock files.

Sources of truth, in order:
  * **/packages.lock.json  (NuGet; SHA512 content hashes included)
  * samples/Cargo.lock     (guest workspace; path-only deps carry no hash)

The output is a deterministic CycloneDX 1.6 JSON document covering every
third-party component that ships with a dist, so consumers and vuln
scanners get an exact inventory without re-resolving anything. Each NuGet
component carries its SPDX license expression; see NUGET_LICENSES.

The document goes to stdout unless --output names a file; status lines go
to stderr, so `sbom.py | jq .` works.
"""

import argparse
import json
import pathlib
import sys
import tomllib
import xml.etree.ElementTree as ET

BOM_SPEC_VERSION = "1.6"
PROJECT_NAME = "7dtd-wasm"

# Lock-file entries that are not third-party packages.
NUGET_SKIP_TYPES = {"Project"}

# Directories whose lock files do not describe a shipped artifact: build
# output is transient, and evidence/ is a frozen record of a past playtest
# rather than something `make dist` stages.
NUGET_SKIP_DIRS = ("bin", "obj", "dist", "evidence")

# SPDX license expression per NuGet package id, lowercased. A package that
# reaches a committed lock file without an entry here fails the SBOM build:
# an unrecorded license is the exact gap the inventory exists to close, and
# THIRD-PARTY-NOTICES.md carries the same facts in prose.
NUGET_LICENSES = {
    "indexrange": "MIT",
    "microsoft.codecoverage": "MIT",
    "microsoft.net.test.sdk": "MIT",
    "microsoft.netcore.platforms": "MIT",
    "microsoft.netframework.referenceassemblies": "MIT",
    "microsoft.netframework.referenceassemblies.net48": "MIT",
    "microsoft.testplatform.objectmodel": "MIT",
    "microsoft.testplatform.testhost": "MIT",
    "netstandard.library": "MIT",
    "system.buffers": "MIT",
    "system.memory": "MIT",
    "system.numerics.vectors": "MIT",
    "system.runtime.compilerservices.unsafe": "MIT",
    "wasmtime": "Apache-2.0 WITH LLVM-exception",
    "xunit": "Apache-2.0",
    "xunit.abstractions": "Apache-2.0",
    "xunit.analyzers": "Apache-2.0",
    "xunit.assert": "Apache-2.0",
    "xunit.core": "Apache-2.0",
    "xunit.extensibility.core": "Apache-2.0",
    "xunit.extensibility.execution": "Apache-2.0",
    "xunit.runner.visualstudio": "Apache-2.0",
}


def license_for(name: str) -> str:
    """SPDX expression for a NuGet package id, or fail naming the package."""
    try:
        return NUGET_LICENSES[name.lower()]
    except KeyError:
        raise SystemExit(
            f"sbom: no recorded license for NuGet package {name!r}; add it to "
            f"NUGET_LICENSES in tools/sbom.py and to THIRD-PARTY-NOTICES.md"
        ) from None


def project_version(root: pathlib.Path) -> str:
    """Read the modlet version from ModInfo.xml."""
    modinfo = next(root.glob("src/*/ModInfo.xml"), None)
    if modinfo is None:
        raise SystemExit("sbom: ModInfo.xml not found under src/")
    # ModInfo.xml is a file in this repository, not a guest-supplied
    # document, so it carries no external entity to expand.
    try:
        tag = ET.parse(modinfo).find("Version")  # noqa: S314
    except (OSError, ET.ParseError) as error:
        raise SystemExit(f"sbom: cannot read {modinfo}: {error}") from error
    if tag is None or not tag.get("value"):
        raise SystemExit(f"sbom: no <Version value=...> in {modinfo}")
    return tag.get("value")


def load_json(path: pathlib.Path) -> dict:
    """Parse a lock file, naming the file on a malformed or unreadable one."""
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except OSError as error:
        raise SystemExit(f"sbom: cannot read {path}: {error}") from error
    except json.JSONDecodeError as error:
        raise SystemExit(f"sbom: {path} is not valid JSON: {error}") from error


def load_toml(path: pathlib.Path) -> dict:
    """Parse a TOML file, naming the file on a malformed or unreadable one."""
    try:
        with path.open("rb") as handle:
            return tomllib.load(handle)
    except OSError as error:
        raise SystemExit(f"sbom: cannot read {path}: {error}") from error
    except tomllib.TOMLDecodeError as error:
        raise SystemExit(f"sbom: {path} is not valid TOML: {error}") from error


def nuget_components(lock_path: pathlib.Path) -> list[dict]:
    """Flatten one packages.lock.json into deduplicated components."""
    data = load_json(lock_path)
    found: dict[str, dict] = {}
    for tfm_deps in data.get("dependencies", {}).values():
        for name, info in tfm_deps.items():
            if info.get("type") in NUGET_SKIP_TYPES:
                continue
            version = info.get("resolved")
            if not version:
                continue
            comp = {
                "type": "library",
                "name": name,
                "version": version,
                "purl": f"pkg:nuget/{name.lower()}@{version}",
                "licenses": [{"license": {"id": license_for(name)}}],
            }
            if info.get("contentHash"):
                comp["hashes"] = [{"alg": "SHA-512", "content": info["contentHash"]}]
            found[comp["purl"]] = comp
    return list(found.values())


def cargo_members(samples_dir: pathlib.Path) -> set[str]:
    """Package names of the workspace's own crates (first-party)."""
    names = set()
    for manifest in samples_dir.rglob("Cargo.toml"):
        name = load_toml(manifest).get("package", {}).get("name")
        if name:
            names.add(name)
    return names


def cargo_components(cargo_lock: pathlib.Path) -> list[dict]:
    """Components from Cargo.lock, excluding first-party workspace crates."""
    data = load_toml(cargo_lock)
    members = cargo_members(cargo_lock.parent)
    comps = []
    for pkg in data.get("package", []):
        if pkg["name"] in members:
            continue
        comps.append(
            {
                "type": "library",
                "name": pkg["name"],
                "version": pkg["version"],
                "purl": f"pkg:cargo/{pkg['name']}@{pkg['version']}",
            }
        )
    return comps


def build_bom(root: pathlib.Path) -> dict:
    """Build the full CycloneDX document for the repository at root."""
    components: dict[str, dict] = {}
    for lock in sorted(root.rglob("packages.lock.json")):
        if any(part in NUGET_SKIP_DIRS for part in lock.parts):
            continue
        for comp in nuget_components(lock):
            components[comp["purl"]] = comp
    cargo_lock = root / "samples" / "Cargo.lock"
    if cargo_lock.exists():
        for comp in cargo_components(cargo_lock):
            components[comp["purl"]] = comp
    return {
        "bomFormat": "CycloneDX",
        "specVersion": BOM_SPEC_VERSION,
        "version": 1,
        "metadata": {
            "component": {
                "type": "application",
                "name": PROJECT_NAME,
                "version": project_version(root),
            },
        },
        "components": sorted(components.values(), key=lambda c: c["purl"]),
    }


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(
        prog="sbom.py",
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("--root", type=pathlib.Path,
                        default=pathlib.Path(__file__).resolve().parent.parent,
                        help="repository to inventory (default: the tool's own repo)")
    parser.add_argument("-o", "--output", type=pathlib.Path, metavar="FILE",
                        help="write JSON here instead of stdout")
    args = parser.parse_args(argv)

    if not args.root.is_dir():
        print(f"sbom: {args.root} is not a directory", file=sys.stderr)
        return 2
    bom = build_bom(args.root)
    text = json.dumps(bom, indent=2) + "\n"
    if args.output:
        try:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(text, encoding="utf-8")
        except OSError as error:
            print(f"sbom: cannot write {args.output}: {error}", file=sys.stderr)
            return 2
        print(f"sbom: wrote {len(bom['components'])} components to {args.output}",
              file=sys.stderr)
    else:
        print(text, end="")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
