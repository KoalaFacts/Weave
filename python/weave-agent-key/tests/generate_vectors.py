"""Regenerates tests/vectors/agent-key-vectors.json from the committed TEST-ONLY key with a fixed clock, jti and token.

Run from the repository root after a deliberate wire change:

    python python/weave-agent-key/tests/generate_vectors.py

Ed25519 is deterministic, so an unchanged helper reproduces the committed file byte for byte; test_vectors.py checks that.
The key is read from the existing file and is never replaced: it is public and must never be registered or used anywhere else."""
import json
import os
import sys
import tempfile
import uuid
from pathlib import Path
from unittest.mock import patch
from urllib.parse import parse_qs

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from weave_agent_key import AccessToken, HelperOptions, HttpResponse, access_token, fetch_with_key, load_key  # noqa: E402

VECTORS_PATH = Path(__file__).resolve().parent / 'vectors' / 'agent-key-vectors.json'
COMMENT = 'TEST-ONLY wire vectors. The key below was generated solely for these tests, is public, and must never be registered or used anywhere else.'
ORIGIN = 'https://relay.example'
NOW = 1793491200
JTI = '6a1f4d2e-8b3c-4e5f-9a7b-0c1d2e3f4a5b'
ACCOUNT_ID = '0f8fad5b-d9cb-469f-a165-70867728950e'
ACCESS_TOKEN = 'vector.access-token_~+/0123=='
KEY_LIST_PATH = '/v1/keys?limit=10'


def _jws(compact: str) -> dict:
    signing_input, _, signature = compact.rpartition('.')
    return {'signingInput': signing_input, 'signature': signature}


def build() -> dict:
    """The vectors the current helper produces for the committed test-only key."""
    test_only_key = json.loads(VECTORS_PATH.read_text('utf-8'))['testOnlyKey']
    with tempfile.TemporaryDirectory(prefix='weave-agent-key-vectors-') as directory:
        path = Path(directory) / 'test-only.jwk'
        path.write_text(json.dumps(test_only_key), 'utf-8')
        if os.name != 'nt':
            os.chmod(path, 0o600)
        key = load_key(path)

    sent = []

    def relay(request):
        sent.append(request)
        if request.url.endswith('/v1/token'):
            return HttpResponse(200, {}, json.dumps({'access_token': ACCESS_TOKEN, 'token_type': 'DPoP', 'expires_in': 900}).encode())
        return HttpResponse(200, {}, b'{"items":[]}')

    # A fractional clock proves that iat is truncated to whole seconds.
    options = HelperOptions(transport=relay, now=lambda: NOW + 0.75)
    with patch('weave_agent_key.client.uuid.uuid4', return_value=uuid.UUID(JTI)):
        issued = access_token(ORIGIN, key, ACCOUNT_ID, options)
        fetch_with_key(ORIGIN, key, AccessToken(ACCESS_TOKEN, issued.expires_at, ACCOUNT_ID), 'get', KEY_LIST_PATH, options=options)
    token_request, key_list = sent
    return {
        '_comment': COMMENT,
        'testOnlyKey': test_only_key,
        'kid': key.kid,
        'origin': ORIGIN,
        'now': NOW,
        'jti': JTI,
        'accountId': ACCOUNT_ID,
        'accessToken': ACCESS_TOKEN,
        'tokenRequest': {
            'url': token_request.url,
            'assertion': _jws(parse_qs(token_request.body.decode())['client_assertion'][0]),
            'dpop': _jws(token_request.headers['DPoP']),
        },
        'keyList': {'path': KEY_LIST_PATH, 'url': key_list.url, 'dpop': _jws(key_list.headers['DPoP'])},
    }


def render(vectors: dict) -> str:
    return json.dumps(vectors, indent=2, ensure_ascii=False) + '\n'


if __name__ == '__main__':
    VECTORS_PATH.write_text(render(build()), 'utf-8', newline='\n')
