#!/usr/bin/env python3
"""Print a version the build is pinned to, read from the file that declares it.

The Makefile needs three versions and none of them is declared in the
Makefile: ruff is pinned by pyproject.toml, the staged Wasmtime engine must
match the version the committed NuGet lock file resolved, and the guest
Rust channel is the one rustup itself reads from samples/rust-toolchain.toml.
Reading them here keeps each number in one file, parses them as the format
they are actually written in (TOML, JSON, TOML again), and gives the build
one tested place to look instead of a regex quoted into a shell.

Usage: pinned.py <ruff|wasmtime|rust> [--root DIR]

The version goes to stdout and nothing else does, so the Makefile can
substitute it directly. A name that is not declared, or a declaration that
does not parse, is an error on stderr with a non-zero exit: a version the
build silently invented would put the wrong engine in a dist, or check a
developer machine against the wrong gate.
"""

import argparse
import json
import pathlib
import sys
import tomllib

ROOT = pathlib.Path(__file__).resolve().parent.parent

# The NuGet package whose version the staged native engine has to match.
WASMTIME_PACKAGE = "Wasmtime"
# The lock file that resolves it, relative to the repository root. One file
# per project; the library is the only one that pulls the native runtime in.
WASMTIME_LOCK = pathlib.Path("src/HordeForge.WasmHost/packages.lock.json")
RUST_TOOLCHAIN = pathlib.Path("samples/rust-toolchain.toml")


class PinError(Exception):
    """A version could not be read, or is not one this tool knows."""


def _load_toml(path: pathlib.Path) -> dict:
    try:
        with path.open("rb") as handle:
            return tomllib.load(handle)
    except OSError as error:
        raise PinError(f"cannot read {path}: {error}") from error
    except tomllib.TOMLDecodeError as error:
        raise PinError(f"{path} is not valid TOML: {error}") from error


def ruff_version(root: pathlib.Path) -> str:
    """The ruff release the tools lint gate is pinned to (without '==')."""
    required = _load_toml(root / "pyproject.toml")["tool"]["ruff"].get("required-version", "")
    version = required.removeprefix("==").strip()
    if not version:
        raise PinError("pyproject.toml: [tool.ruff] required-version names no version")
    return version


def wasmtime_version(root: pathlib.Path) -> str:
    """The Wasmtime version the committed NuGet lock file resolved."""
    path = root / WASMTIME_LOCK
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except OSError as error:
        raise PinError(f"cannot read {path}: {error}") from error
    except json.JSONDecodeError as error:
        raise PinError(f"{path} is not valid JSON: {error}") from error

    resolved: set[str] = set()
    for tfm_deps in data.get("dependencies", {}).values():
        entry = tfm_deps.get(WASMTIME_PACKAGE)
        if isinstance(entry, dict) and entry.get("resolved"):
            resolved.add(entry["resolved"])
    if not resolved:
        raise PinError(
            f"{path}: no resolved {WASMTIME_PACKAGE} entry (restore it, or the pin is gone)"
        )
    if len(resolved) > 1:
        versions = ", ".join(sorted(resolved))
        raise PinError(
            f"{path}: {WASMTIME_PACKAGE} resolves to {versions}; the engine staged is one file"
        )
    return resolved.pop()


def rust_version(root: pathlib.Path) -> str:
    """The Rust channel rustup installs for the guests."""
    channel = _load_toml(root / RUST_TOOLCHAIN).get("toolchain", {}).get("channel", "")
    if not channel:
        raise PinError(f"{RUST_TOOLCHAIN}: [toolchain] channel names no channel")
    return channel


PINS = {
    "ruff": ruff_version,
    "wasmtime": wasmtime_version,
    "rust": rust_version,
}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="pinned.py",
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("pin", choices=sorted(PINS), help="which pinned version to print")
    parser.add_argument(
        "--root",
        type=pathlib.Path,
        default=ROOT,
        help="repository to read (default: the tool's own repo)",
    )
    args = parser.parse_args(argv)
    try:
        version = PINS[args.pin](args.root)
    except (PinError, KeyError) as error:
        print(f"pinned: {error}", file=sys.stderr)
        return 1
    print(version)
    return 0


if __name__ == "__main__":
    sys.exit(main())
