"""Cross-language wire vectors. Ed25519 signatures are deterministic, so for the TEST-ONLY key in tests/vectors/ the Python and
TypeScript helpers must produce byte-identical JWK thumbprints, JWS signing inputs and signatures. The TypeScript side is
typescript/agent-key/test/vectors.test.ts, which also verifies these proofs with the mailbox Worker's own DPoP/JWS code."""
import json
import os
import tempfile
import unittest
import uuid
from pathlib import Path
from unittest.mock import patch
from urllib.parse import parse_qs

import support  # noqa: F401 - imports the package from this checkout and skips without cryptography
import generate_vectors
from weave_agent_key import AccessToken, HelperOptions, HttpResponse, access_token, fetch_with_key, load_key

VECTORS_TEXT = (Path(__file__).parent / 'vectors' / 'agent-key-vectors.json').read_bytes().decode('utf-8')
VECTORS = json.loads(VECTORS_TEXT)


def compact(vector):
    return vector['signingInput'] + '.' + vector['signature']


class VectorTests(unittest.TestCase):
    def test_vectors_match_typescript(self):
        with tempfile.TemporaryDirectory(prefix='weave-agent-key-vectors-') as directory:
            path = Path(directory) / 'test-only.jwk'
            path.write_text(json.dumps(VECTORS['testOnlyKey']), 'utf-8')
            if os.name != 'nt':
                os.chmod(path, 0o600)
            key = load_key(path)
        self.assertEqual(key.kid, VECTORS['kid'])

        sent = []

        def relay(request):
            sent.append(request)
            if request.url.endswith('/v1/token'):
                return HttpResponse(200, {}, json.dumps({'access_token': VECTORS['accessToken'], 'token_type': 'DPoP', 'expires_in': 900}).encode())
            return HttpResponse(200, {}, b'{"items":[]}')

        options = HelperOptions(transport=relay, now=lambda: VECTORS['now'] + 0.75)
        with patch('weave_agent_key.client.uuid.uuid4', return_value=uuid.UUID(VECTORS['jti'])):
            issued = access_token(VECTORS['origin'], key, VECTORS['accountId'], options)
            fetch_with_key(VECTORS['origin'], key, AccessToken(VECTORS['accessToken'], issued.expires_at, VECTORS['accountId']),
                           'get', VECTORS['keyList']['path'], options=options)

        token_request, key_list = sent
        self.assertEqual(token_request.url, VECTORS['origin'] + '/v1/token')
        self.assertEqual(parse_qs(token_request.body.decode())['client_assertion'], [compact(VECTORS['tokenRequest']['assertion'])])
        self.assertEqual(token_request.headers['DPoP'], compact(VECTORS['tokenRequest']['dpop']))
        self.assertEqual(key_list.headers['Authorization'], 'DPoP ' + VECTORS['accessToken'])
        self.assertEqual(key_list.headers['DPoP'], compact(VECTORS['keyList']['dpop']))

    def test_generator_reproduces_committed_vectors(self):
        self.assertEqual(generate_vectors.render(generate_vectors.build()), VECTORS_TEXT)


if __name__ == '__main__':
    unittest.main()
