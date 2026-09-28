#!/usr/bin/env python3
"""Unit tests for tools/doccheck.py. Run: python3 -m unittest discover -s tools"""

import contextlib
import io
import os
import pathlib
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

import doccheck


class LineErrorsTest(unittest.TestCase):
    def test_clean_line_has_no_hits(self):
        self.assertEqual(doccheck.line_errors("hello survivor"), [])

    def test_em_dash_hit(self):
        self.assertIn("em dash found", doccheck.line_errors("a \u2014 b"))

    def test_en_dash_hit(self):
        self.assertIn("em dash found", doccheck.line_errors("a \u2013 b"))

    def test_ai_attribution_hit(self):
        self.assertIn("possible AI attribution", doccheck.line_errors("written by Claude"))

    def test_plain_by_phrase_passes(self):
        self.assertEqual(doccheck.line_errors("stand by me"), [])


class LinkTargetTest(unittest.TestCase):
    def test_existing_file_passes(self):
        root = pathlib.Path(tempfile.mkdtemp())
        target = root / "other.md"
        target.write_text("hi", encoding="utf-8")
        page = root / "page.md"
        page.write_text("x", encoding="utf-8")
        self.assertFalse(doccheck.link_target_broken(page, "other.md"))

    def test_missing_file_fails(self):
        root = pathlib.Path(tempfile.mkdtemp())
        page = root / "page.md"
        page.write_text("x", encoding="utf-8")
        self.assertTrue(doccheck.link_target_broken(page, "gone.md"))

    def test_external_and_anchor_links_skipped(self):
        root = pathlib.Path(tempfile.mkdtemp())
        page = root / "page.md"
        page.write_text("x", encoding="utf-8")
        for target in (
            "https://example.com/x",
            "http://e/x",
            "#frag",
            "mailto:a@b.c",
            "",
            "other.md#frag",
        ):
            with self.subTest(target=target):
                if target == "other.md#frag":
                    (root / "other.md").write_text("x", encoding="utf-8")
                self.assertFalse(doccheck.link_target_broken(page, target))


class TodoViolationTest(unittest.TestCase):
    def test_checkboxes_pass(self):
        self.assertFalse(doccheck.is_todo_violation("- [ ] do it"))
        self.assertFalse(doccheck.is_todo_violation("- [x] done"))

    def test_bare_todo_fails(self):
        self.assertTrue(doccheck.is_todo_violation("- TODO do it"))
        self.assertTrue(doccheck.is_todo_violation("- todo do it"))

    def test_plain_bullets_pass(self):
        self.assertFalse(doccheck.is_todo_violation("- just a bullet"))
        self.assertFalse(doccheck.is_todo_violation("TODO without bullet"))


class UnindexedDocsTest(unittest.TestCase):
    """docs/INDEX.md is the contract index, so a document outside it fails."""

    def build(self, root, index_body, names):
        docs = root / "docs"
        docs.mkdir(parents=True, exist_ok=True)
        for name in names:
            (docs / name).write_text("body\n", encoding="utf-8")
        (docs / "INDEX.md").write_text(index_body, encoding="utf-8")
        return docs

    def test_listed_document_passes(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            self.build(root, "| [A.md](A.md) | owns a |\n", ["A.md"])
            self.assertEqual(doccheck.unindexed_docs(root), [])

    def test_unlisted_document_fails(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            self.build(root, "| [A.md](A.md) | owns a |\n", ["A.md", "B.md"])
            self.assertEqual([p.name for p in doccheck.unindexed_docs(root)], ["B.md"])

    def test_index_does_not_need_to_list_itself(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            self.build(root, "no links\n", ["INDEX.md"])
            self.assertEqual(doccheck.unindexed_docs(root), [])

    def test_subdirectories_are_left_to_their_own_index(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            docs = self.build(root, "| [A.md](A.md) | owns a |\n", ["A.md"])
            (docs / "adrs").mkdir()
            (docs / "adrs" / "0001-x.md").write_text("x\n", encoding="utf-8")
            self.assertEqual(doccheck.unindexed_docs(root), [])

    def test_root_without_an_index_is_inert(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            (root / "notes.md").write_text("clean\n", encoding="utf-8")
            self.assertEqual(doccheck.unindexed_docs(root), [])

    def test_main_reports_an_unindexed_document(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            self.build(root, "no links\n", ["A.md"])
            out, err = io.StringIO(), io.StringIO()
            with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
                code = doccheck.main(["--root", str(root)])
        self.assertEqual(code, 1)
        self.assertIn("not listed in docs/INDEX.md", err.getvalue())


class MainTest(unittest.TestCase):
    """The gate reports on stderr and leaves stdout empty for the caller."""

    def run_main(self, root):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = doccheck.main(["--root", str(root)])
        return code, out.getvalue(), err.getvalue()

    def test_clean_tree_exits_zero_with_empty_stdout(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            (root / "notes.md").write_text("clean survivor notes\n", encoding="utf-8")
            code, out, err = self.run_main(root)
        self.assertEqual(code, 0)
        self.assertEqual(out, "")
        self.assertIn("doccheck: ok", err)

    def test_findings_go_to_stderr_and_exit_one(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            (root / "notes.md").write_text("bad \u2014 dash\n", encoding="utf-8")
            code, out, err = self.run_main(root)
        self.assertEqual(code, 1)
        self.assertEqual(out, "")
        self.assertIn("em dash found", err)
        self.assertIn("doccheck:", err)

    def test_missing_root_fails_instead_of_reporting_ok(self):
        with tempfile.TemporaryDirectory() as tmp:
            missing = pathlib.Path(tmp) / "nope"
            code, out, err = self.run_main(missing)
        self.assertEqual(code, 2)
        self.assertEqual(out, "")
        self.assertIn("is not a directory", err)
        self.assertNotIn("doccheck: ok", err)

    def test_unreadable_file_is_a_finding_not_a_crash(self):
        if os.geteuid() == 0:
            self.skipTest("root bypasses file permissions")
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            locked = root / "locked.md"
            locked.write_text("clean\n", encoding="utf-8")
            locked.chmod(0o000)
            try:
                (root / "notes.md").write_text("bad \u2014 dash\n", encoding="utf-8")
                code, _out, err = self.run_main(root)
            finally:
                locked.chmod(0o600)
        self.assertEqual(code, 1)
        self.assertIn("cannot read file", err)
        # The rest of the tree is still checked after the unreadable file.
        self.assertIn("em dash found", err)


if __name__ == "__main__":
    unittest.main()
