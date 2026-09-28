#!/usr/bin/env python3
"""Public surface gate for the published host library.

`HordeForge.WasmHost` is the one artifact here a consumer takes a dependency
on, so a member that disappears from it is a break in the sense CONTRIBUTING
defines, and a break has to bump the minor digit. Nothing in the repository
could tell a removed public member from an added one, which is how
`WasmModHost.TryInit` came off the surface in the 0.3.1 patch slot.

This tool reads the public and protected surface of `src/HordeForge.WasmHost`
and compares it with a committed baseline, `tools/api-surface.txt`. Any
difference fails, including a removal. Accepting a change is a deliberate act:
the author confirms the bump and the changelog entry, then regenerates the
baseline with `--update`, which is a reviewable diff in the same commit.

It is a surface diff, not a compiler: a member's body, doc comment and
accessor implementation are not part of the surface, so a change inside one
does not fail here. A signature change, a rename, a removed or added member,
a changed base type or a new accessibility all do.

Exit code is non-zero when the surface and the baseline disagree.
"""

import argparse
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
BASELINE = "tools/api-surface.txt"
LIBRARY = pathlib.Path("src") / "HordeForge.WasmHost"

BLOCK_COMMENT = re.compile(r"/\*.*?\*/", re.DOTALL)
LINE_COMMENT = re.compile(r"//[^\n]*")
STRING_LITERAL = re.compile(r'"(?:\\.|[^"\\\n])*"')
CHAR_LITERAL = re.compile(r"'(?:\\.|[^'\\\n])*'")
TYPE_DECL = re.compile(r"\b(?:class|struct|interface|enum|record)\s+([A-Za-z_][A-Za-z0-9_]*)")
ACCESS = re.compile(r"\b(public|protected)\b")
# A member of an interface with no access modifier: a return type and a name
# followed by a parameter list, a property, or an event.
IMPLICIT_MEMBER = re.compile(r"^(?:[A-Za-z_][\w.<>,\[\]?]*\s+)?[A-Za-z_]\w*\s*[({]")
# A signature ends at the body, the statement, or an expression-bodied
# member's arrow. A bare ">" is a generic argument, not the end.
TERMINATOR = re.compile(r";|\{|=>")
# A constructor's ": this(...)" or ": base(...)" clause, which is a body
# detail rather than part of the signature.
CTOR_INITIALIZER = re.compile(r":\s*(?:this|base)\s*\(")
# get { ... } -> get; : the accessor keyword is surface, its body is not.
ACCESSOR = re.compile(r"\b(get|set|init|add|remove)\s*\{")
ACCESSOR_LIST = re.compile(r"\{\s*((?:(?:get|set|init|add|remove)\s*;\s*)+)\}")


def strip_accessor_bodies(text: str) -> str:
    """Reduce every block accessor to its keyword.

    Braces are counted rather than matched by a regex: an accessor body can
    nest them (a getter that takes a lock), and a pattern that stops at the
    first "}" leaves the rest of the body in the text, which then reads as
    further members.
    """
    out: list[str] = []
    position = 0
    while (match := ACCESSOR.search(text, position)) is not None:
        out.append(text[position : match.start()])
        out.append(match.group(1) + ";")
        depth = 0
        index = match.end() - 1
        while index < len(text):
            if text[index] == "{":
                depth += 1
            elif text[index] == "}":
                depth -= 1
                if depth == 0:
                    break
            index += 1
        position = index + 1
    out.append(text[position:])
    return "".join(out)


def strip_noise(text: str) -> str:
    """Remove comments, literals and accessor bodies from the source text.

    A property's accessors are surface ({ get; set; } differs from
    { get; }), the code inside them is not, so a block accessor is reduced to
    its keyword.
    """
    text = BLOCK_COMMENT.sub("", text)
    text = LINE_COMMENT.sub("", text)
    text = STRING_LITERAL.sub('""', CHAR_LITERAL.sub("''", text))
    text = strip_accessor_bodies(text)
    # An accessor list on its own lines is collapsed onto the declaration so
    # every property is read as one line.
    return ACCESSOR_LIST.sub(lambda m: " { " + " ".join(m.group(1).split()) + " }", text)


def strip_constructor_initializer(signature: str) -> str:
    """Cut a constructor initializer off a normalized signature.

    An overload that chains with `: this(...)` or `: base(...)` has the same
    signature as one that does not, so recording the clause would report
    every chained constructor as removed and re-added. The argument list is
    balanced rather than matched with a regex: it can nest, and it can hold
    a collection initializer.
    """
    match = CTOR_INITIALIZER.search(signature)
    if match is None:
        return signature
    depth = 0
    index = match.end() - 1
    while index < len(signature):
        char = signature[index]
        if char in "([{":
            depth += 1
        elif char in ")]}":
            depth -= 1
            if depth == 0:
                break
        index += 1
    return signature[: match.start()].rstrip()


def normalize(text: str) -> str:
    """Collapse a signature to one line.

    A parameter list written across lines and the same list written on one
    line are the same member, so the space a wrap leaves after "(" and before
    ")" is removed with the rest of the whitespace.
    """
    text = re.sub(r"\s+", " ", text).strip()
    return strip_constructor_initializer(re.sub(r"\(\s+", "(", re.sub(r"\s+\)", ")", text)))


def source_files(root: pathlib.Path) -> list[pathlib.Path]:
    library = root / LIBRARY
    return sorted(
        path
        for path in library.rglob("*.cs")
        if not {"bin", "obj"} & set(path.relative_to(library).parts)
    )


def surface(root: pathlib.Path) -> list[str]:
    """Return the sorted public surface of the library, one entry per member.

    Walks each file with a brace-depth stack. A type name is pushed when its
    body opens, and every public member is attributed to the innermost
    enclosing type. An interface member with no access modifier is public in
    C#, so those are recorded too, marked implicit.
    """
    entries: set[str] = set()
    for path in source_files(root):
        relative = path.relative_to(root).as_posix()
        text = strip_noise(path.read_text(encoding="utf-8"))
        depth = 0
        # A type name is remembered until the brace that opens its body, which
        # is the next line under this repository's brace style.
        pending_type = ""
        pending_type_is_interface = False
        # Types open at the depth their body starts; index by that depth.
        names: dict[int, tuple[str, bool]] = {}
        # A signature can wrap over several lines (a long parameter list); the
        # second half is carried here until a terminator ends it.
        pending_signature = ""
        pending_owner = ""
        for raw in text.splitlines():
            line = raw.strip()
            if not line:
                continue
            name = names.get(depth)
            owner = name[0] if name else None
            is_interface = bool(name and name[1])
            match = TYPE_DECL.search(line)
            if match:
                prefix = line[: match.start()]
                pending_type = match.group(1)
                pending_type_is_interface = "interface" in line
                pending_type_is_public = not {"private", "internal"} & set(
                    prefix.replace("partial", "").split()
                )
            for char in line:
                if char == "{":
                    depth += 1
                    if pending_type:
                        names[depth] = (pending_type, pending_type_is_interface)
                        if pending_type_is_public:
                            entries.add(f"{relative}: type {pending_type}")
                        pending_type = ""
                elif char == "}":
                    names.pop(depth, None)
                    depth = max(0, depth - 1)
            if owner is None:
                continue
            if pending_signature and ACCESS.match(line):
                # The previous signature never saw its terminator and this
                # line starts a new member; record what was collected.
                entries.add(f"{relative}: {pending_owner}.{normalize(pending_signature)}")
                pending_signature = ""
            if pending_signature:
                candidate = pending_signature + " " + line
            else:
                access = ACCESS.search(line)
                if access:
                    candidate = line[access.start() :]
                elif is_interface and IMPLICIT_MEMBER.match(line):
                    candidate = "public " + line
                else:
                    continue
                pending_owner = owner
            if TYPE_DECL.search(candidate):
                # A nested type declaration, not a member: its own body and
                # members are walked with the depth stack.
                pending_signature = ""
                continue
            split = TERMINATOR.search(candidate)
            if split is None:
                pending_signature = candidate
                continue
            if candidate[split.start()] == "{" and "(" not in candidate[: split.start()]:
                close = candidate.find("}", split.start())
                if close < 0:
                    pending_signature = candidate[: split.start()]
                    continue
                pending_signature = ""
                entries.add(f"{relative}: {pending_owner}.{normalize(candidate[: close + 1])}")
                continue
            pending_signature = ""
            signature = normalize(candidate[: split.start()])
            if len(signature) < len("public") + 2:
                continue
            if TYPE_DECL.search(signature):
                continue
            entries.add(f"{relative}: {pending_owner}.{signature}")
    return sorted(entries)


def diff(expected: list[str], actual: list[str]) -> tuple[list[str], list[str]]:
    expected_set, actual_set = set(expected), set(actual)
    return (
        sorted(expected_set - actual_set),
        sorted(actual_set - expected_set),
    )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="apicheck.py",
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
        "--update", action="store_true", help="rewrite the baseline from the current source"
    )
    args = parser.parse_args(argv)

    baseline = args.root / BASELINE
    library = args.root / LIBRARY
    if not library.is_dir():
        print(f"apicheck: {library} does not exist", file=sys.stderr)
        return 2

    current = surface(args.root)
    if args.update:
        baseline.parent.mkdir(parents=True, exist_ok=True)
        baseline.write_text("\n".join(current) + "\n", encoding="utf-8")
        print(f"apicheck: wrote {len(current)} entries to {BASELINE}", file=sys.stderr)
        return 0

    if not baseline.is_file():
        print(
            f"apicheck: {BASELINE} is missing; run apicheck.py --update "
            "and review the diff it writes",
            file=sys.stderr,
        )
        return 1
    expected = baseline.read_text(encoding="utf-8").splitlines()
    removed, added = diff(expected, current)
    if not removed and not added:
        print(f"apicheck: {len(current)} public entries match {BASELINE}", file=sys.stderr)
        return 0

    for entry in removed:
        print(f"apicheck: removed {entry}", file=sys.stderr)
    for entry in added:
        print(f"apicheck: added   {entry}", file=sys.stderr)
    if removed:
        print(
            "apicheck: the public surface shrank; a removal is a breaking "
            "change, so the minor digit must move and the changelog needs a "
            "(breaking) entry naming the replacement",
            file=sys.stderr,
        )
    print(
        "apicheck: update the baseline with apicheck.py --update only once "
        "the bump and the entry are in place",
        file=sys.stderr,
    )
    return 1


if __name__ == "__main__":
    sys.exit(main())
