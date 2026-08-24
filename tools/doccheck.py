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

errors = 0
warnings = 0
text_files = []


def walk():
    for path in ROOT.rglob("*"):
        if not path.is_file():
            continue
        if any(part in SKIP_DIRS or part.startswith(".") and part != ".gitignore" for part in path.relative_to(ROOT).parts):
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
        if EM_DASH.search(line):
            errors += 1
            print(f"{path}:{lineno}: em dash found")
        if AI_ATTR.search(line):
            errors += 1
            print(f"{path}:{lineno}: possible AI attribution")
        # Internal links must resolve to an existing file.
        for target in LINK.findall(line):
            if target.startswith(("http://", "https://", "#", "mailto:")):
                continue
            link = target.split("#")[0].strip()
            if not link:
                continue
            resolved = (path.parent / link).resolve()
            if not resolved.exists():
                errors += 1
                print(f"{path}:{lineno}: broken link -> {target}")
    # TODO list items must use the checkbox format.
    for lineno, line in enumerate(text.splitlines(), 1):
        if line.lstrip().startswith("- [ ]") or line.lstrip().startswith("- [x]"):
            continue
        if re.match(r"^\s*- (TODO|todo)", line):
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
