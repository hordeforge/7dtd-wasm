#!/usr/bin/env python3
"""Unit tests for tools/doccheck.py. Run: python3 -m unittest discover -s tools"""

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
        self.assertIn("possible AI attribution",
                      doccheck.line_errors("written by Claude"))

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
        for target in ("https://example.com/x", "http://e/x", "#frag",
                       "mailto:a@b.c", "", "other.md#frag"):
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


if __name__ == "__main__":
    unittest.main()
