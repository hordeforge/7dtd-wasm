#!/usr/bin/env python3
"""Docs quality gate for 7dtd-wasm.

Checks that shipped text follows the workspace rules:
  * no em dashes anywhere in the repo's text files
  * no AI attribution (no "generated/written/assisted by <tool>")
  * every markdown link to a local file points at an existing file
  * TODO items follow the "- [ ]" checkbox format

Exit code is non-zero when any check fails, so CI and "make check" can gate.
"""

import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent

# Directories that never contain shipped text.
SKIP_DIRS = {
    ".git", ".cargo", ".rustup", "bin", "obj", "dist", "target",
    "__pycache__",
}

EM_DASH = re.compile("\u2014|\u2013")  # em and en dash
AI_ATTR = re.compile(
    r"\b(generated|written|authored|assisted|created|drafted|produced)\s+by\s+"
    r"(an?\s+)?(ai|llm|claude|chatgpt|gpt|bard|copilot|gemini|agent)",
    re.IGNORECASE,
)
LINK = re.compile(r"\[[^\]]*\]\(([^)]+)\)")
CHECKBOX = re.compile(r"^\s*- \[[ x]\]")

# A TODO-looking list item that is not a checkbox (checked in markdown).
TODO_BARE = re.compile(r"^\s*- (TODO|todo)")

errors = 0
warnings = 0
text_files = []


def line_errors(line: str) -> list[str]:
    """Rule hits for one line: em dash, AI attribution. Pure for tests."""
    hits = []
    if EM_DASH.search(line):
        hits.append("em dash found")
    if AI_ATTR.search(line):
        hits.append("possible AI attribution")
    return hits


def link_target_broken(path: pathlib.Path, target: str) -> bool:
    """True when a markdown link target names a missing local file."""
    if target.startswith(("http://", "https://", "#", "mailto:")):
        return False
    link = target.split("#")[0].strip()
    if not link:
        return False
    return not (path.parent / link).resolve().exists()


def is_todo_violation(line: str) -> bool:
    """True for a TODO list item not using the checkbox format."""
    if line.lstrip().startswith("- [ ]") or line.lstrip().startswith("- [x]"):
        return False
    return TODO_BARE.match(line) is not None


def walk():
    for path in ROOT.rglob("*"):
        if not path.is_file():
            continue
        if any(
            part in SKIP_DIRS or (part.startswith(".") and part != ".gitignore")
            for part in path.relative_to(ROOT).parts
        ):
            continue
        # "makefile" sits in the name check, not the suffix set: a file
        # named Makefile has no dot suffix, so it would never match.
        if path.suffix.lower() in {
            ".md", ".txt", ".cs", ".csproj", ".rs", ".toml", ".py",
            ".json", ".xml", ".yml", ".yaml", ".sh", ".sln",
        } or path.name.lower() == "makefile":
            text_files.append(path)
        if path.suffix.lower() == ".md":
            check_markdown(path)


def check_markdown(path):
    global errors, warnings
    text = path.read_text(encoding="utf-8", errors="replace")
    for lineno, line in enumerate(text.splitlines(), 1):
        for hit in line_errors(line):
            errors += 1
            print(f"{path}:{lineno}: {hit}")
        # Internal links must resolve to an existing file.
        for target in LINK.findall(line):
            if link_target_broken(path, target):
                errors += 1
                print(f"{path}:{lineno}: broken link -> {target}")
    # TODO list items must use the checkbox format.
    for lineno, line in enumerate(text.splitlines(), 1):
        if is_todo_violation(line):
            errors += 1
            print(f"{path}:{lineno}: TODO item must use '- [ ]' checkbox format")


def check_plain_text():
    global errors
    for path in text_files:
        text = path.read_text(encoding="utf-8", errors="replace")
        for lineno, line in enumerate(text.splitlines(), 1):
            if EM_DASH.search(line):
                errors += 1
                print(f"{path}:{lineno}: em dash found")


def main():
    walk()
    check_plain_text()
    if errors:
        print(f"doccheck: {errors} error(s) found")
        return 1
    print("doccheck: ok")
    return 0


if __name__ == "__main__":
    sys.exit(main())
