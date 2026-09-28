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
    repository URL and its type)
  * `TargetFrameworks` still declares the frameworks README.md promises
  * `PackageLicenseExpression` matches the license in LICENSE
  * `PackageReadmeFile` is declared and the file it names is packed
  * THIRD-PARTY-NOTICES.md and LICENSE are packed, so the link the notices
    open on ("see [LICENSE](LICENSE)") resolves inside the package

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

# The frameworks a consumer gets an assembly for, as README.md states them.
# Dropping one is a silent change to what the package installs, so the
# declaration, the README and this list have to move together.
SHIPPED_FRAMEWORKS = ("netstandard2.0", "net8.0")


class GateError(Exception):
    """An input the gate reads could not be used at all.

    A malformed manifest or an unreadable LICENSE is a finding, not a crash:
    the traceback names a line inside this tool and leaves the operator
    guessing which file is at fault, where the named message says both what
    to fix and that the package metadata is unverified.
    """


def project(root: pathlib.Path) -> ET.Element:
    """Parse the library manifest and return its root element."""
    # The manifest is a file in this repository, not a guest-supplied
    # document, so it needs no hardened parser.
    path = root / CSPROJ
    try:
        return ET.parse(path).getroot()  # noqa: S314
    except ET.ParseError as error:
        raise GateError(f"{CSPROJ} is not well-formed XML: {error}") from error
    except OSError as error:
        raise GateError(f"cannot read {CSPROJ}: {error}") from error


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
    """SPDX id of the repository license, read from the LICENSE header.

    An empty or blank LICENSE has no header line to read. Indexing the first
    line without checking turns that into an IndexError from inside the gate;
    naming the file instead reports the finding the gate exists to raise.
    """
    path = root / LICENSE
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except OSError as error:
        raise GateError(f"cannot read {LICENSE}: {error}") from error
    except UnicodeDecodeError as error:
        raise GateError(f"{LICENSE} is not valid UTF-8: {error}") from error
    first = next((line.strip() for line in lines if line.strip()), "")
    if not first:
        raise GateError(f"{LICENSE} is empty, so the license the package declares cannot be confirmed")
    return first.removesuffix(" License")


def shipped_frameworks(manifest: ET.Element) -> set[str]:
    """Frameworks the manifest packs an assembly for.

    Both spellings are read: a single-target library declares
    <TargetFramework>, a multi-target one the semicolon-separated
    <TargetFrameworks>.
    """
    declared = declared_property(manifest, "TargetFrameworks") or declared_property(
        manifest, "TargetFramework"
    )
    if not declared:
        return set()
    return {framework.strip() for framework in declared.split(";") if framework.strip()}


def check(root: pathlib.Path) -> list[str]:
    """Findings for the library manifest; empty when it is complete."""
    manifest = project(root)
    findings = [
        f"{CSPROJ}: <{name}> is missing or empty"
        for name in ("PackageId", "Description", "RepositoryUrl", "RepositoryType")
        if not declared_property(manifest, name)
    ]

    packed_frameworks = shipped_frameworks(manifest)
    if not packed_frameworks:
        findings.append(f"{CSPROJ}: no <TargetFrameworks> is declared")
    findings.extend(
        f"{CSPROJ}: does not pack {framework}, which README.md promises"
        for framework in SHIPPED_FRAMEWORKS
        if framework not in packed_frameworks
    )

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
    if LICENSE.name not in packed_files(manifest):
        findings.append(
            f"{CSPROJ}: {LICENSE.name} is not packed, so the link "
            f"{NOTICES.name} opens on does not resolve inside the package"
        )

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
    except (GateError, OSError) as error:
        # A readable repository whose manifest or license cannot be used is a
        # failed check, not a usage error: the path it names is the finding.
        print(f"packcheck: {error}", file=sys.stderr)
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
