"""Exercise evidence availability without network credentials or external requests."""
import base64
from email.message import Message
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import urllib.error

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("evidence", ROOT / "scripts/check_dependency_review_evidence.py")
evidence = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(evidence)
BASE, HEAD = "1" * 40, "2" * 40
CHANGE = {"change_type": "added", "manifest": "src/Weave.csproj"}
NEXT = '<https://api.github.com/ignored>; rel="next"'


class Reply(io.BytesIO):
    def __init__(self, data=b"[]", warning=None, link=None, status=200):
        super().__init__(data)
        self.status = status
        self.headers = Message()
        if warning is not None:
            self.headers["X-GitHub-Dependency-Graph-Snapshot-Warnings"] = warning
        if link is not None:
            self.headers["Link"] = link


class Opener:
    def __init__(self, *replies):
        self.replies = list(replies)
        self.requests = []

    def open(self, request, timeout):
        self.requests.append((request, timeout))
        value = self.replies.pop(0)
        if isinstance(value, Exception):
            raise value
        return value


class EvidenceApiTests(unittest.TestCase):
    def check(self, opener):
        return evidence.inspect_comparison("owner/repo", BASE, HEAD, "synthetic-token", opener)

    def test_empty_delta_without_warnings_is_available_not_missing_evidence(self):
        self.assertEqual(self.check(Opener(Reply())), {"dependency_changes": 0, "pages": 1})

    def test_nonempty_delta_is_counted(self):
        self.assertEqual(self.check(Opener(Reply(json.dumps([CHANGE]).encode())))["dependency_changes"], 1)

    def test_missing_base_or_head_snapshot_blocks_even_an_empty_delta(self):
        for side in ("base", "head"):
            with self.subTest(side=side):
                warning = "No snapshots were found for the " + side + " SHA"
                reply = Reply(warning=base64.b64encode(warning.encode()).decode())
                with self.assertRaises(evidence.EvidenceUnavailable) as caught:
                    self.check(Opener(reply))
                self.assertEqual(caught.exception.code, "missing-snapshot-evidence")
                self.assertEqual(caught.exception.warnings, (warning,))

    def test_bad_or_blank_encoded_warning_is_not_ignored(self):
        for header in ("%%%", "====", "//8=", "IA==", "A" * 16385):
            with self.subTest(header=header[:8]), self.assertRaises(evidence.EvidenceUnavailable):
                self.check(Opener(Reply(warning=header)))

    def test_missing_evidence_on_later_page_blocks_the_whole_comparison(self):
        warning = base64.b64encode(b"missing head").decode()
        opener = Opener(Reply(json.dumps([CHANGE] * 100).encode(), link=NEXT), Reply(warning=warning))
        with self.assertRaises(evidence.EvidenceUnavailable):
            self.check(opener)
        self.assertEqual(len(opener.requests), 2)

    def test_pagination_never_follows_a_foreign_next_url(self):
        opener = Opener(Reply(json.dumps([CHANGE]).encode(), link='<https://evil.invalid/>; rel="next"'), Reply())
        self.assertEqual(self.check(opener)["pages"], 2)
        self.assertTrue(all(req.full_url.startswith("https://api.github.com/repos/owner/repo/")
                            for req, _ in opener.requests))
        self.assertTrue(opener.requests[1][0].full_url.endswith("page=2"))

    def test_non_success_status_is_unavailable(self):
        for status in (301, 401, 403, 404, 429, 500):
            with self.subTest(status=status), self.assertRaises(evidence.EvidenceUnavailable):
                self.check(Opener(Reply(status=status)))

    def test_errors_do_not_include_private_response_or_credentials(self):
        failures = [urllib.error.URLError("private details synthetic-token"),
                    TimeoutError("synthetic-token"),
                    urllib.error.HTTPError("https://api.github.com", 403, "private details", {}, io.BytesIO())]
        for failure in failures:
            with self.subTest(error=type(failure).__name__), self.assertRaises(evidence.EvidenceUnavailable) as caught:
                self.check(Opener(failure))
            self.assertNotIn("synthetic-token", str(caught.exception))
            self.assertNotIn("private", str(caught.exception))

    def test_malformed_or_wrong_schema_response_is_unavailable(self):
        for body in (b"{bad", b"null", b"{}", b"[null]", b"[{}]", b"\xff",
                     json.dumps([{"change_type": "unexpected", "manifest": "src/Weave.csproj"}]).encode()):
            with self.subTest(body=body[:20]), self.assertRaises(evidence.EvidenceUnavailable):
                self.check(Opener(Reply(body)))

    def test_response_is_bounded_without_content_length(self):
        reply = Reply(b" " * (evidence.MAX_PAGE_BYTES + 10))
        with self.assertRaises(evidence.EvidenceUnavailable) as caught:
            self.check(Opener(reply))
        self.assertEqual(caught.exception.code, "comparison-response-too-large")
        self.assertTrue(reply.closed)

    def test_page_limit_is_not_successful_partial_review(self):
        with patch.object(evidence, "MAX_PAGES", 2), self.assertRaises(evidence.EvidenceUnavailable) as caught:
            self.check(Opener(*(Reply(json.dumps([CHANGE] * 100).encode(), link=NEXT) for _ in range(2))))
        self.assertEqual(caught.exception.code, "comparison-pagination-limit")

    def test_complete_unpaginated_delta_over_one_hundred_is_not_invalid(self):
        opener = Opener(Reply(json.dumps([CHANGE] * 280).encode()))
        try:
            result = self.check(opener)
        except evidence.EvidenceUnavailable as error:
            self.fail(f"Complete bounded comparison was rejected: {error.code}")
        self.assertEqual(result, {"dependency_changes": 280, "pages": 1})
        self.assertEqual(len(opener.requests), 1)

    def test_exactly_one_hundred_without_next_does_not_invent_a_page(self):
        opener = Opener(Reply(json.dumps([CHANGE] * 100).encode()), Reply())
        self.assertEqual(self.check(opener), {"dependency_changes": 100, "pages": 1})
        self.assertEqual(len(opener.requests), 1)

    def test_total_entry_budget_is_preserved_for_complete_large_responses(self):
        with self.assertRaises(evidence.EvidenceUnavailable) as caught:
            self.check(Opener(Reply(json.dumps([CHANGE] * 2001).encode())))
        self.assertEqual(caught.exception.code, "comparison-change-limit")

    def test_total_entry_budget_applies_across_explicit_pages(self):
        opener = Opener(Reply(json.dumps([CHANGE] * 100).encode(), link=NEXT),
                        Reply(json.dumps([CHANGE] * 1901).encode()))
        with self.assertRaises(evidence.EvidenceUnavailable) as caught:
            self.check(opener)
        self.assertEqual(caught.exception.code, "comparison-change-limit")
        self.assertEqual(len(opener.requests), 2)

    def test_large_response_still_checks_every_entry_and_snapshot_warning(self):
        body = json.dumps([CHANGE] * 200 + [{"change_type": "unexpected", "manifest": "x"}]).encode()
        with self.assertRaises(evidence.EvidenceUnavailable) as caught:
            self.check(Opener(Reply(body)))
        self.assertEqual(caught.exception.code, "invalid-comparison-response")
        warning = base64.b64encode(b"missing head").decode()
        with self.assertRaises(evidence.EvidenceUnavailable) as caught:
            self.check(Opener(Reply(json.dumps([CHANGE] * 280).encode(), warning=warning)))
        self.assertEqual(caught.exception.code, "missing-snapshot-evidence")

    def test_invalid_identity_is_rejected_before_any_request(self):
        for repo, base, head in (("owner/repo?x", BASE, HEAD), ("../repo", BASE, HEAD),
                                 ("owner/repo", "main", HEAD), ("owner/repo", BASE, "$(echo bad)")):
            opener = Opener()
            with self.subTest(repo=repo), self.assertRaises(evidence.EvidenceUnavailable):
                evidence.inspect_comparison(repo, base, head, "synthetic-token", opener)
            self.assertEqual(opener.requests, [])

    def test_missing_or_injected_token_is_rejected(self):
        for token in ("", "bad\r\nheader"):
            with self.subTest(token=token), self.assertRaises(evidence.EvidenceUnavailable):
                evidence.inspect_comparison("owner/repo", BASE, HEAD, token, Opener())

    def test_redirect_handler_refuses_redirect(self):
        self.assertIsNone(evidence.NoRedirect().redirect_request(None, None, 302, "", {}, "https://evil.invalid/"))

    def test_cli_failure_records_exact_refs_and_does_not_log_token(self):
        with tempfile.TemporaryDirectory() as directory:
            report = Path(directory) / "evidence.json"
            summary = Path(directory) / "summary.md"
            env = {"GITHUB_REPOSITORY": "owner/repo", "PR_BASE_SHA": BASE, "PR_HEAD_SHA": HEAD,
                   "GH_TOKEN": "synthetic-token", "GITHUB_STEP_SUMMARY": str(summary)}
            output = io.StringIO()
            with patch.dict(os.environ, env), patch.object(evidence, "inspect_comparison",
                    side_effect=evidence.EvidenceUnavailable("missing-snapshot-evidence", ("missing head",))), \
                    patch("sys.stdout", output):
                result = evidence.main(["--report", str(report)])
            self.assertEqual(result, 1)
            document = json.loads(report.read_text())
            self.assertEqual((document["base_sha"], document["head_sha"]), (BASE, HEAD))
            self.assertEqual(document["status"], "unavailable")
            self.assertNotIn("synthetic-token", report.read_text() + output.getvalue() + summary.read_text())
            self.assertIn("review blocked", summary.read_text())


if __name__ == "__main__":
    unittest.main()
