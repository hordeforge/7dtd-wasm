#!/usr/bin/env python3
"""Package metadata gate for the publishable host library.

`HordeForge.WasmHost` is the one artifact here a consumer installs with a
package manager, so the metadata in its manifest is part of the product: a
missing readme leaves the nuget.org listing bare, a license expression that
disagrees with LICENSE misdeclares the terms, and the third-party notices
have to travel inside the package because Wasmtime is Apache-2.0 WITH
LLVM-exception. Nothing in the repository connected those three to the
manifest, so a well-meaning edit could drop any of them silently and only
surface at publish time.

This reads `src/HordeForge.WasmHost/HordeForge.WasmHost.csproj` and checks:

  * the identity fields a listing needs are present (id, description,
    repository URL)
  * `PackageLicenseExpression` matches the license in LICENSE
  * `PackageReadmeFile` is declared and the file it names is packed
  * THIRD-PARTY-NOTICES.md is packed

The declared version is not checked here; tools/versioncheck.py owns the
agreement between the manifest, the modlet and the changelog.

Exit code is non-zero when any check fails, so CI and "make check" can gate.
"""

import argparse
import pathlib
import sys
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parent.parent

CSPROJ = pathlib.Path("src") / "HordeForge.WasmHost" / "HordeForge.WasmHost.csproj"
LICENSE = pathlib.Path("LICENSE")
NOTICES = pathlib.Path("THIRD-PARTY-NOTICES.md")


def project(root: pathlib.Path) -> ET.Element:
    """Parse the library manifest and return its root element."""
    # The manifest is a file in this repository, not a guest-supplied
    # document, so it needs no hardened parser.
    return ET.parse(root / CSPROJ).getroot()  # noqa: S314


def declared_property(root: ET.Element, name: str) -> str | None:
    """Read a MSBuild property element, or None when it is not declared."""
    element = root.find(f".//{name}")
    return None if element is None else (element.text or "").strip()


def packed_files(root: ET.Element) -> set[str]:
    """Names of the files the manifest packs, as a consumer sees them."""
    names = set()
    for element in root.findall(".//None"):
        if (element.get("Pack") or "").lower() != "true":
            continue
        include = element.get("Include")
        if include:
            names.add(pathlib.PurePosixPath(include).name)
    return names


def license_id(root: pathlib.Path) -> str:
    """SPDX id of the repository license, read from the LICENSE header."""
    first = (root / LICENSE).read_text(encoding="utf-8").splitlines()[0].strip()
    return first.removesuffix(" License")


def check(root: pathlib.Path) -> list[str]:
    """Findings for the library manifest; empty when it is complete."""
    manifest = project(root)
    findings = [
        f"{CSPROJ}: <{name}> is missing or empty"
        for name in ("PackageId", "Description", "RepositoryUrl")
        if not declared_property(manifest, name)
    ]

    declared = declared_property(manifest, "PackageLicenseExpression")
    actual = license_id(root)
    if declared != actual:
        findings.append(
            f"{CSPROJ}: <PackageLicenseExpression> is {declared or 'unset'}, "
            f"but LICENSE is {actual}"
        )

    readme = declared_property(manifest, "PackageReadmeFile")
    if not readme:
        findings.append(f"{CSPROJ}: <PackageReadmeFile> is not declared")
    elif readme not in packed_files(manifest):
        findings.append(f"{CSPROJ}: {readme} is declared but not packed")

    if NOTICES.name not in packed_files(manifest):
        findings.append(f"{CSPROJ}: {NOTICES.name} is not packed")

    return findings


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="packcheck.py",
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument(
        "--root",
        type=pathlib.Path,
        default=ROOT,
        help="repository to check (default: the tool's own repo)",
    )
    args = parser.parse_args(argv)

    # A mistyped --root is a usage error (exit 2), like the other tools,
    # not a failed check: it says nothing about the manifest.
    if not args.root.is_dir():
        print(f"packcheck: {args.root} is not a directory", file=sys.stderr)
        return 2

    try:
        findings = check(args.root)
    except OSError as error:
        # A readable repository whose manifest is gone is a failed check, not
        # a usage error: the path it names is the finding.
        print(f"packcheck: cannot read the manifest: {error}", file=sys.stderr)
        return 1

    for finding in findings:
        print(f"packcheck: {finding}", file=sys.stderr)
    if findings:
        print(f"packcheck: {len(findings)} package metadata problem(s)", file=sys.stderr)
        return 1
    # The summary is a diagnostic, not data: stdout stays empty so a caller
    # piping it never has to tell gate chatter from output.
    print(f"packcheck: ok ({CSPROJ} declares a complete package)", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
