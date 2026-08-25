#!/usr/bin/env python3
"""Generate a CycloneDX SBOM from the committed dependency lock files.

Sources of truth, in order:
  * **/packages.lock.json  (NuGet; SHA512 content hashes included)
  * samples/Cargo.lock     (guest workspace; path-only deps carry no hash)

The output is a deterministic CycloneDX 1.6 JSON document covering every
third-party component that ships with a dist, so consumers and vuln
scanners get an exact inventory without re-resolving anything.

Usage: sbom.py [-o OUT.json] [--root REPO_ROOT]
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


def project_version(root: pathlib.Path) -> str:
    """Read the modlet version from ModInfo.xml."""
    modinfo = next(root.glob("src/*/ModInfo.xml"), None)
    if modinfo is None:
        raise SystemExit("sbom: ModInfo.xml not found under src/")
    tag = ET.parse(modinfo).find("Version")
    if tag is None or not tag.get("value"):
        raise SystemExit(f"sbom: no <Version value=...> in {modinfo}")
    return tag.get("value")


def nuget_components(lock_path: pathlib.Path) -> list[dict]:
    """Flatten one packages.lock.json into deduplicated components."""
    data = json.loads(lock_path.read_text(encoding="utf-8"))
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
            }
            if info.get("contentHash"):
                comp["hashes"] = [{"alg": "SHA-512", "content": info["contentHash"]}]
            found[comp["purl"]] = comp
    return list(found.values())


def cargo_members(samples_dir: pathlib.Path) -> set[str]:
    """Package names of the workspace's own crates (first-party)."""
    names = set()
    for manifest in samples_dir.rglob("Cargo.toml"):
        with manifest.open("rb") as fh:
            name = tomllib.load(fh).get("package", {}).get("name")
        if name:
            names.add(name)
    return names


def cargo_components(cargo_lock: pathlib.Path) -> list[dict]:
    """Components from Cargo.lock, excluding first-party workspace crates."""
    with cargo_lock.open("rb") as fh:
        data = tomllib.load(fh)
    members = cargo_members(cargo_lock.parent)
    comps = []
    for pkg in data.get("package", []):
        if pkg["name"] in members:
            continue
        comps.append({
            "type": "library",
            "name": pkg["name"],
            "version": pkg["version"],
            "purl": f"pkg:cargo/{pkg['name']}@{pkg['version']}",
        })
    return comps


def build_bom(root: pathlib.Path) -> dict:
    """Build the full CycloneDX document for the repository at root."""
    components: dict[str, dict] = {}
    for lock in sorted(root.rglob("packages.lock.json")):
        if any(part in ("bin", "obj", "dist") for part in lock.parts):
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
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=pathlib.Path,
                        default=pathlib.Path(__file__).resolve().parent.parent)
    parser.add_argument("-o", "--output", type=pathlib.Path,
                        help="write JSON here instead of stdout")
    args = parser.parse_args(argv)

    bom = build_bom(args.root)
    text = json.dumps(bom, indent=2) + "\n"
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(text, encoding="utf-8")
        print(f"sbom: wrote {len(bom['components'])} components to {args.output}")
    else:
        print(text, end="")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
