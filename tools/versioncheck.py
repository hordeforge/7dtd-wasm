#!/usr/bin/env python3
"""Release consistency gate for 7dtd-wasm.

The repository declares its shipped version in three places and they must
always agree:

  * src/GameBridge/ModInfo.xml       (the version the game server shows)
  * src/HordeForge.WasmHost/*.csproj (<Version> of the publishable package)
  * CHANGELOG.md                     (newest released "## [X.Y.Z]" section)

A disagreement means a tag, the artifact, and the notes can each describe a
different release, so any drift fails here instead of at tag time (the
release workflow enforces the same rule against vX.Y.Z tags).

Exit code is non-zero when the declarations are missing or disagree.
"""

import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
MODINFO = ROOT / "src" / "GameBridge" / "ModInfo.xml"
CSPROJ = ROOT / "src" / "HordeForge.WasmHost" / "HordeForge.WasmHost.csproj"
CHANGELOG = ROOT / "CHANGELOG.md"

# <Version value="1.2.3" /> in the game modlet manifest.
MODINFO_VERSION = re.compile(r"<Version\s+value=\"([^\"]+)\"")
# <Version>1.2.3</Version> in the library manifest; the literal "<Version>"
# open tag cannot match <PackageReference ... Version=...> or <LangVersion>.
CSPROJ_VERSION = re.compile(r"<Version>([^<]+)</Version>")
# Newest released section header; "## Unreleased" has no brackets.
CHANGELOG_SECTION = re.compile(r"^## \[([^\]]+)\]", re.MULTILINE)


def read_version(modinfo: pathlib.Path) -> str:
    match = MODINFO_VERSION.search(modinfo.read_text(encoding="utf-8"))
    if not match:
        raise ValueError(f"{modinfo}: no <Version value=\"...\"> found")
    return match.group(1).strip()


def package_version(csproj: pathlib.Path) -> str:
    match = CSPROJ_VERSION.search(csproj.read_text(encoding="utf-8"))
    if not match:
        raise ValueError(f"{csproj}: no <Version>...</Version> found")
    return match.group(1).strip()


def released_version(changelog: pathlib.Path) -> str:
    match = CHANGELOG_SECTION.search(changelog.read_text(encoding="utf-8"))
    if not match:
        raise ValueError(f"{changelog}: no '## [X.Y.Z]' release section found")
    return match.group(1).strip()


def main() -> int:
    try:
        versions = {
            str(MODINFO.relative_to(ROOT)): read_version(MODINFO),
            str(CSPROJ.relative_to(ROOT)): package_version(CSPROJ),
            str(CHANGELOG.relative_to(ROOT)): released_version(CHANGELOG),
        }
    except (OSError, ValueError) as error:
        print(f"versioncheck: {error}")
        return 1

    for source, version in sorted(versions.items()):
        print(f"versioncheck: {source} ships {version}")

    unique = set(versions.values())
    if len(unique) != 1:
        print("versioncheck: version declarations disagree; tag, artifact, "
              "and changelog would describe different releases")
        return 1
    print("versioncheck: ok")
    return 0


if __name__ == "__main__":
    sys.exit(main())
