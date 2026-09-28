#!/usr/bin/env python3
"""Release consistency gate for 7dtd-wasm.

The repository declares its shipped version in three places and they must
always agree:

  * src/GameBridge/ModInfo.xml       (the version the game server shows)
  * src/HordeForge.WasmHost/*.csproj (<Version> of the publishable package)
  * CHANGELOG.md                     (newest released "## [X.Y.Z]" section)

A disagreement means a tag, the artifact, and the notes can each describe a
different release, so any drift fails here instead of at tag time. The
release workflow passes --tag, which additionally requires every declaration
to ship the version the vX.Y.Z tag names, so one set of rules and one set of
error messages cover both gates.

Exit code is non-zero when the declarations are missing, disagree, or do
not match --tag.
"""

import argparse
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent

# <Version value="1.2.3" /> in the game modlet manifest.
MODINFO_VERSION = re.compile(r"<Version\s+value=\"([^\"]+)\"")
# <Version>1.2.3</Version> in the library manifest; the literal "<Version>"
# open tag cannot match <PackageReference ... Version=...> or <LangVersion>.
CSPROJ_VERSION = re.compile(r"<Version>([^<]+)</Version>")
# Newest released section header; "## Unreleased" has no brackets.
CHANGELOG_SECTION = re.compile(r"^## \[([^\]]+)\]", re.MULTILINE)


def read_declaration(path: pathlib.Path) -> str:
    """One declaration's text, naming the file on a read or decode failure.

    All three declarations are read the same way, so they are read the same
    way here: a decode error names a byte offset and nothing else, and
    without the file the operator cannot tell which declaration it came from.
    """
    try:
        return path.read_text(encoding="utf-8")
    except OSError as error:
        raise ValueError(f"cannot read {path}: {error}") from error
    except UnicodeDecodeError as error:
        raise ValueError(f"{path} is not valid UTF-8: {error}") from error


def read_version(modinfo: pathlib.Path) -> str:
    match = MODINFO_VERSION.search(read_declaration(modinfo))
    if not match:
        raise ValueError(f'{modinfo}: no <Version value="..."> found')
    return match.group(1).strip()


def package_version(csproj: pathlib.Path) -> str:
    match = CSPROJ_VERSION.search(read_declaration(csproj))
    if not match:
        raise ValueError(f"{csproj}: no <Version>...</Version> found")
    return match.group(1).strip()


def released_version(changelog: pathlib.Path) -> str:
    match = CHANGELOG_SECTION.search(read_declaration(changelog))
    if not match:
        raise ValueError(f"{changelog}: no '## [X.Y.Z]' release section found")
    return match.group(1).strip()


TAG_PREFIX = "v"


def tag_mismatches(tag: str, versions: dict[str, str]) -> list[str]:
    """Report every declaration that does not ship the version the tag names.

    A tag without the "v" prefix has no version to compare against and is
    itself the error, so it comes back as a single mismatch line.
    """
    if not tag.startswith(TAG_PREFIX):
        return [f"tag {tag!r} does not start with {TAG_PREFIX!r}"]
    wanted = tag[len(TAG_PREFIX) :]
    return [
        f"tag {tag} but {source} ships {version}"
        for source, version in sorted(versions.items())
        if version != wanted
    ]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="versioncheck.py",
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument(
        "--root",
        type=pathlib.Path,
        default=ROOT,
        help="repository to check (default: the tool's own repo)",
    )
    parser.add_argument(
        "--tag",
        help="also require every declaration to ship the version this vX.Y.Z tag names",
    )
    args = parser.parse_args(argv)

    # A mistyped --root is a usage error (exit 2), like the other tools,
    # not a failed check: it says nothing about the declarations.
    if not args.root.is_dir():
        print(f"versioncheck: {args.root} is not a directory", file=sys.stderr)
        return 2

    modinfo = args.root / "src" / "GameBridge" / "ModInfo.xml"
    csproj = args.root / "src" / "HordeForge.WasmHost" / "HordeForge.WasmHost.csproj"
    changelog = args.root / "CHANGELOG.md"
    try:
        versions = {
            str(modinfo.relative_to(args.root)): read_version(modinfo),
            str(csproj.relative_to(args.root)): package_version(csproj),
            str(changelog.relative_to(args.root)): released_version(changelog),
        }
    except (OSError, ValueError) as error:
        # read_declaration names the file for a read or decode failure; the
        # OSError here is the path itself failing to resolve.
        print(f"versioncheck: {error}", file=sys.stderr)
        return 1

    for source, version in sorted(versions.items()):
        print(f"versioncheck: {source} ships {version}", file=sys.stderr)

    unique = set(versions.values())
    if len(unique) != 1:
        print(
            "versioncheck: version declarations disagree; tag, artifact, "
            "and changelog would describe different releases",
            file=sys.stderr,
        )
        return 1
    if args.tag is not None:
        mismatches = tag_mismatches(args.tag, versions)
        if mismatches:
            for mismatch in mismatches:
                print(f"versioncheck: {mismatch}", file=sys.stderr)
            print("versioncheck: make them match before tagging", file=sys.stderr)
            return 1
        print(f"versioncheck: ok ({args.tag})", file=sys.stderr)
        return 0
    print("versioncheck: ok", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
