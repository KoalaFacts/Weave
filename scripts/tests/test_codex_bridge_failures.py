"""No credentials/model service needed: exercise the bridge's real subprocess boundary."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from urllib import error
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / 'examples/governed-tools/codex_bridge.py'
SPEC = importlib.util.spec_from_file_location('bridge_failures', SOURCE)
bridge = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(bridge)


class BridgeFailureTests(unittest.TestCase):
    def test_lost_post_response_keeps_uuid_receipt_and_never_retries(self):
        identifier = '89f0b55c9a314192a40751bb85da3c43'
        with tempfile.TemporaryDirectory() as temp:
            agent = bridge.Bridge('http://127.0.0.1:9401', 'pilot', 'files', Path(temp), 'synthetic_capability')
            absent = {'invocation': {'http_status': 404, 'result': None},
                      'approval': {'http_status': 404, 'result': None}}
            with patch.object(agent, 'status', return_value=absent), \
                 patch.object(agent.http, 'open', side_effect=error.URLError('private-upstream-detail')) as network:
                result = agent.call_tool('submit_write', {'invocation_id': identifier, 'path': 'out.md', 'content': 'model content'})
            self.assertIsNone(result['http_status'])
            self.assertIn('Outcome unconfirmed', result['next'])
            self.assertNotIn('private-upstream-detail', json.dumps(result))
            self.assertEqual(network.call_count, 1)
            self.assertEqual(network.call_args.args[0].method, 'POST')
            receipt = json.loads((Path(temp) / (identifier + '.json')).read_text())
            self.assertEqual(receipt['invocationId'], result['invocation_id'])
            self.assertEqual(set(receipt), {'invocationId', 'inputSha256'})
            with patch.object(agent, 'status', return_value=absent), patch.object(agent.http, 'open') as network:
                agent.call_tool('submit_write', {'invocation_id': identifier, 'path': 'out.md', 'content': 'model content'})
                network.assert_not_called()

    def test_remote_cleartext_and_credential_urls_are_rejected(self):
        for url in ('http://example.com', 'https://user:secret@example.com', 'https://example.com/?key=x',
                    'https://example.com/api', 'https://example.com/#fragment'):
            with self.subTest(url=url), self.assertRaises(bridge.BridgeError):
                bridge.endpoint(url)
        self.assertEqual(bridge.endpoint('https://example.test/'), 'https://example.test')
        self.assertIsNone(bridge.NoRedirect().redirect_request(None, None, 307, '', {}, 'https://other.invalid'))

    def test_empty_and_duplicate_json_never_become_saved_requests(self):
        with self.assertRaises(bridge.BridgeError):
            bridge.load(b'{"a":1,"a":2}')
        with self.assertRaises(bridge.BridgeError):
            bridge.load(b'{"a":NaN}')
        with self.assertRaises(bridge.BridgeError):
            bridge.load(b' ' * (bridge.MAX_BYTES + 1))

    def test_admin_environment_is_rejected_before_protocol_or_http(self):
        with tempfile.TemporaryDirectory() as temp:
            env = {k: os.environ[k] for k in ('PATH', 'LANG', 'SystemRoot', 'SystemDrive') if k in os.environ}
            env.update({'WEAVE_AGENT_CAPABILITY': 'synthetic', 'WEAVE_OPERATOR_KEY': 'do-not-leak-this-private-value'})
            result = subprocess.run([sys.executable, str(SOURCE), '--url', 'http://127.0.0.1:1',
                                     '--workspace', 'pilot', '--requests', temp], input='', text=True,
                                    capture_output=True, timeout=5, env=env, cwd=temp)
            self.assertEqual(result.returncode, 2)
            self.assertEqual(result.stdout, '')
            self.assertNotIn('do-not-leak', result.stderr)
            self.assertFalse((Path(temp) / '%SystemDrive%').exists())
            self.assertEqual(list(Path(temp).iterdir()), [])


if __name__ == '__main__':
    unittest.main()
