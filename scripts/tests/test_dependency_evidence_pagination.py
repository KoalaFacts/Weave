"""Retain the action's complete comparison view within explicit evidence limits."""
import json
import importlib.util
from pathlib import Path
import threading
import unittest
import urllib.parse
import urllib.request

from test_dependency_evidence_api import BASE, HEAD, CHANGE, NEXT, Opener, Reply, evidence


class EvidencePaginationTests(unittest.TestCase):
    def check(self, opener):
        return evidence.inspect_comparison('owner/repo', BASE, HEAD, 'synthetic-token', opener)

    def test_observed_page_size_views_include_every_action_manifest(self):
        fixture = json.loads((Path(__file__).parent / 'fixtures/dependency-comparison-pagination.json').read_text())
        records = fixture['records']

        class PageViews(Opener):
            def open(self, request, timeout):
                self.requests.append((request, timeout))
                query = urllib.parse.parse_qs(urllib.parse.urlsplit(request.full_url).query)
                if query['per_page'] == ['100']:
                    return Reply(json.dumps([records[i] for i in fixture['per_page_100']]).encode())
                if query['per_page'] != ['5']:
                    raise AssertionError('Unexpected comparison page size')
                page = int(query['page'][0])
                return Reply(json.dumps([records[i] for i in fixture['per_page_5'][page - 1]]).encode(),
                             link=NEXT if page == 1 else None)

        opener = PageViews()
        result = self.check(opener)

        self.assertEqual(result['dependency_records'], [records[i] for i in fixture['action_parsed']])
        self.assertEqual((result['dependency_changes'], result['pages']), (9, 2))
        self.assertEqual(len(opener.requests), 2)
        self.assertTrue(all('per_page=5&' in request.full_url for request, _ in opener.requests))

    def test_four_hundred_full_pages_retain_two_thousand_records(self):
        opener = Opener(*(Reply(json.dumps([CHANGE] * 5).encode(),
                                link=NEXT if page < 400 else None) for page in range(1, 401)))

        result = self.check(opener)

        self.assertEqual((result['dependency_changes'], result['pages']), (2000, 400))
        self.assertEqual(len(result['dependency_records']), 2000)
        self.assertTrue(opener.requests[-1][0].full_url.endswith('&page=400'))

    def test_two_thousand_pages_with_a_continuation_are_not_partial_success(self):
        opener = Opener(*(Reply(json.dumps([CHANGE] * 5).encode(), link=NEXT) for _ in range(evidence.MAX_PAGES)), Reply())

        with self.assertRaises(evidence.EvidenceUnavailable) as caught:
            self.check(opener)

        self.assertEqual(caught.exception.code, 'comparison-pagination-limit')
        self.assertEqual(len(opener.requests), evidence.MAX_PAGES)

    def test_ten_thousand_and_first_record_is_still_rejected(self):
        opener = Opener(Reply(json.dumps([CHANGE] * (evidence.MAX_CHANGES + 1)).encode()))

        with self.assertRaises(evidence.EvidenceUnavailable) as caught:
            self.check(opener)

        self.assertEqual(caught.exception.code, 'comparison-change-limit')
        self.assertEqual(len(opener.requests), 1)

    def test_many_small_pages_cannot_expand_the_old_forty_mib_total_body_budget(self):
        body = b'[]' + b' ' * (2 * 1024 * 1024 - 2)
        opener = Opener(*(Reply(body, link=NEXT) for _ in range(21)), Reply())

        with self.assertRaises(evidence.EvidenceUnavailable) as caught:
            self.check(opener)

        self.assertEqual(caught.exception.code, 'comparison-total-response-too-large')
        self.assertEqual(len(opener.requests), 21)

    def test_acceptance_peer_keeps_independent_and_action_inputs_separate_at_the_same_page_size(self):
        path = Path(__file__).resolve().parents[1] / 'acceptance/dependency_policy.py'
        spec = importlib.util.spec_from_file_location('dependency_policy_acceptance', path)
        acceptance = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(acceptance)
        action_record = {**CHANGE, 'manifest': 'action-only.lock'}
        server = acceptance.Peer([CHANGE], False, [action_record])
        thread = threading.Thread(target=lambda: server.serve_forever(poll_interval=0.01), daemon=True)
        thread.start()
        try:
            endpoint = f'/repos/fixture/repo/dependency-graph/compare/{BASE}...{HEAD}?per_page=5&page=1'
            request = urllib.request.Request('https://api.github.com' + endpoint,
                                             headers={'User-Agent': 'weave-dependency-evidence'})

            with acceptance.LoopbackOpener(server.server_port).open(request, timeout=5) as response:
                independent = json.load(response)
            local = urllib.request.build_opener(urllib.request.ProxyHandler({}))
            with local.open(f'http://127.0.0.1:{server.server_port}{endpoint}', timeout=5) as response:
                action = json.load(response)

            self.assertEqual(independent, [CHANGE])
            self.assertEqual(action, [action_record])
            self.assertEqual(server.unexpected, [])
        finally:
            server.shutdown()
            server.server_close()
            thread.join(5)
        self.assertFalse(thread.is_alive())


if __name__ == '__main__':
    unittest.main()
