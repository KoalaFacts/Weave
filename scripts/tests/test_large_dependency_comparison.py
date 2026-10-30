"""Regression: exact-base dependency comparisons can exceed 2,000 records after a layout move."""
import json
import unittest

from test_dependency_evidence_api import BASE, HEAD, CHANGE, NEXT, Opener, Reply, evidence


class LargeComparisonEvidenceTests(unittest.TestCase):
    def check(self, opener):
        return evidence.inspect_comparison("owner/repo", BASE, HEAD, "synthetic-token", opener)

    def test_complete_2105_distinct_records_are_preserved(self):
        records = [{**CHANGE, "name": f"moved-component-{index}",
                    "package_url": f"pkg:nuget/moved-component-{index}@1.0.0",
                    "license": "MIT"} for index in range(2105)]
        opener = Opener(Reply(json.dumps(records).encode()))
        report = self.check(opener)
        self.assertEqual(report["dependency_changes"], len(records))
        self.assertEqual(report["dependency_records"], records)
        self.assertEqual(report["pages"], 1)
        self.assertEqual(len(opener.requests), 1)

    def test_complete_421_explicit_pages_are_not_truncated_at_page_400(self):
        opener = Opener(*(Reply(json.dumps([{**CHANGE, "manifest": f"manifest-{page}-{item}"} for item in range(5)]).encode(),
                                link=NEXT if page < 421 else None) for page in range(1, 422)))
        report = self.check(opener)
        self.assertEqual((report["dependency_changes"], report["pages"]), (2105, 421))
        self.assertEqual(len(opener.requests), 421)


if __name__ == "__main__":
    unittest.main()
