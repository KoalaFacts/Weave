import base64
import hashlib
import json
import tempfile
import time
import unittest
from pathlib import Path
from urllib.parse import parse_qs

import support  # noqa: F401 - imports the package from this checkout and skips without cryptography
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey
from weave_agent_key import (AccessToken, HelperOptions, HttpResponse, RateLimitedError, WeaveHttpError, access_token, create_key,
                             dpop_proof, fetch_with_key, load_key, register, service_origin)

ORIGIN = 'http://localhost'
ACCOUNT_ID = '0f8fad5b-d9cb-469f-a165-70867728950e'
ISSUED = 'I' * 43


def b64decode(text):
    return base64.urlsafe_b64decode(text + '=' * (-len(text) % 4))


def segments(jws):
    header, payload, signature = jws.split('.')
    return json.loads(b64decode(header)), json.loads(b64decode(payload)), signature


def b64(data):
    return base64.urlsafe_b64encode(data).rstrip(b'=').decode('ascii')


def verify(jws, jwk):
    signing_input, _, signature = jws.rpartition('.')
    Ed25519PublicKey.from_public_bytes(b64decode(jwk['x'])).verify(b64decode(signature), signing_input.encode('ascii'))


def problem(status, code, headers=None):
    return HttpResponse(status, {'content-type': 'application/problem+json', **(headers or {})}, json.dumps({'status': status, 'code': code}).encode())


def ok(value):
    return HttpResponse(200, {'content-type': 'application/json'}, json.dumps(value).encode())


class FakeRelay:
    """Records requests; answers registration, token issue and the key list. `override` can answer first."""

    def __init__(self, key, override=None):
        self.key, self.override, self.calls, self.tokens = key, override, [], 0

    def __call__(self, request):
        path = request.url.split('://', 1)[1].split('/', 1)[1].split('?')[0]
        response = self.override(request, '/' + path) if self.override else None
        if response is None:
            response = self.answer(request, '/' + path)
        self.calls.append((request.method, '/' + path, response.status, request.body))
        return response

    def answer(self, request, path):
        if request.method == 'POST' and path == '/v1/accounts':
            return ok({'accountId': ACCOUNT_ID, 'displayName': None, 'description': None, 'kid': self.key.kid})
        if request.method == 'POST' and path == '/v1/token':
            self.tokens += 1
            return ok({'access_token': f'{ISSUED}{self.tokens}', 'token_type': 'DPoP', 'expires_in': 900})
        if request.method == 'GET' and path == '/v1/keys' and request.headers.get('Authorization', '').startswith('DPoP ' + ISSUED):
            return ok({'items': [{'kid': self.key.kid}]})
        return problem(401, 'unauthenticated')

    def trace(self):
        return [f'{method} {path} {status}' for method, path, status, _ in self.calls]


class ClientTests(unittest.TestCase):
    def setUp(self):
        self._dir = tempfile.TemporaryDirectory(prefix='weave-agent-key-client-')
        path = Path(self._dir.name) / 'agent.jwk'
        create_key(path)
        self.key = load_key(path)

    def tearDown(self):
        self._dir.cleanup()

    def test_dpop_claims_exact(self):
        token, now = 'A' * 43, int(time.time())
        url = ORIGIN + '/v1/inbox?limit=10#top'
        proof = dpop_proof(self.key, 'get', url, token)
        header, claims, _ = segments(proof)
        self.assertEqual(sorted(claims), ['ath', 'htm', 'htu', 'iat', 'jti'])
        self.assertEqual(claims['htu'], ORIGIN + '/v1/inbox')
        self.assertEqual(claims['htm'], 'GET')
        self.assertEqual(claims['ath'], b64(hashlib.sha256(token.encode()).digest()))
        self.assertLessEqual(abs(claims['iat'] - now), 1)
        self.assertEqual(header, {'alg': 'EdDSA', 'typ': 'dpop+jwt', 'jwk': dict(self.key.public_jwk)})
        verify(proof, self.key.public_jwk)
        again = segments(dpop_proof(self.key, 'get', url, token))[1]
        self.assertIsInstance(claims['jti'], str)
        self.assertNotEqual(again['jti'], claims['jti'], 'every proof carries a new jti')
        unbound = segments(dpop_proof(self.key, 'POST', ORIGIN + '/v1/token'))[1]
        self.assertNotIn('ath', unbound)

    def test_helper_register_token_whoami_round_trip(self):
        relay = FakeRelay(self.key)
        options = HelperOptions(transport=relay)
        self.assertEqual(register(ORIGIN, self.key, 'Helper agent', options).account_id, ACCOUNT_ID)
        self.assertEqual(json.loads(relay.calls[0][3]), {'displayName': 'Helper agent'})
        issued = access_token(ORIGIN, self.key, ACCOUNT_ID, options)
        self.assertEqual(issued.token, ISSUED + '1')
        self.assertNotIn(issued.token, repr(issued))
        form = parse_qs(relay.calls[1][3].decode())
        self.assertEqual(form['grant_type'], ['client_credentials'])
        self.assertEqual(form['client_assertion_type'], ['urn:ietf:params:oauth:client-assertion-type:jwt-bearer'])
        self.assertEqual(form['client_id'], [ACCOUNT_ID])
        header, assertion, _ = segments(form['client_assertion'][0])
        self.assertEqual(header, {'alg': 'EdDSA', 'kid': self.key.kid})
        self.assertEqual(assertion['aud'], ORIGIN + '/v1/token')
        self.assertEqual(assertion['iss'], ACCOUNT_ID)
        self.assertEqual(assertion['sub'], ACCOUNT_ID)
        self.assertLessEqual(assertion['exp'] - assertion['iat'], 60)
        verify(form['client_assertion'][0], self.key.public_jwk)
        response, token = fetch_with_key(ORIGIN, self.key, issued, 'GET', '/v1/keys', options=options)
        self.assertEqual(response.status, 200)
        self.assertIs(token, issued)
        self.assertEqual([item['kid'] for item in response.json()['items']], [self.key.kid])
        self.assertEqual(relay.trace(), ['POST /v1/accounts 200', 'POST /v1/token 200', 'GET /v1/keys 200'])

    def test_helper_register_unexpected_kid_refused(self):
        relay = FakeRelay(self.key, lambda request, path: ok({'accountId': ACCOUNT_ID, 'kid': 'another'}))
        with self.assertRaises(WeaveHttpError) as caught:
            register(ORIGIN, self.key, options=HelperOptions(transport=relay))
        self.assertEqual(caught.exception.code, 'unexpected-response')

    def test_helper_401_from_token_renews_once(self):
        relay = FakeRelay(self.key)
        options = HelperOptions(transport=relay)
        stale = AccessToken('A' * 43, '2026-11-01T00:00:00.000Z', ACCOUNT_ID)
        response, renewed = fetch_with_key(ORIGIN, self.key, stale, 'GET', '/v1/keys', options=options)
        self.assertEqual(response.status, 200)
        self.assertNotEqual(renewed.token, stale.token)
        self.assertEqual(renewed.account_id, ACCOUNT_ID)
        self.assertEqual(relay.trace(), ['GET /v1/keys 401', 'POST /v1/token 200', 'GET /v1/keys 200'])
        # A second 401 after renewal is returned, not renewed again.
        refusing = FakeRelay(self.key, lambda request, path: problem(401, 'unauthenticated') if path == '/v1/keys' else None)
        response, _ = fetch_with_key(ORIGIN, self.key, stale, 'GET', '/v1/keys', options=HelperOptions(transport=refusing))
        self.assertEqual(response.status, 401)
        self.assertEqual(refusing.trace(), ['GET /v1/keys 401', 'POST /v1/token 200', 'GET /v1/keys 401'])

    def test_helper_401_from_register_not_retried(self):
        relay = FakeRelay(self.key, lambda request, path: problem(401, 'unauthenticated'))
        with self.assertRaises(WeaveHttpError) as caught:
            register(ORIGIN, self.key, options=HelperOptions(transport=relay))
        self.assertEqual((caught.exception.status, caught.exception.code), (401, 'unauthenticated'))
        self.assertEqual(relay.trace(), ['POST /v1/accounts 401'])
        # Requests authenticated by the key alone are never sent through the token-renewing path.
        token = AccessToken('A' * 43, '2026-11-01T00:00:00.000Z', ACCOUNT_ID)
        for path in ('/v1/accounts', '/v1/key-requests', '/v1/token', '/v1/token?x=1'):
            with self.subTest(path), self.assertRaises(ValueError):
                fetch_with_key(ORIGIN, self.key, token, 'POST', path, {}, HelperOptions(transport=relay))
        self.assertEqual(len(relay.calls), 1)

    def test_helper_429_honours_retry_after_once(self):
        def limited_once(retry_after):
            state = {'limited': False}

            def override(request, path):
                if path != '/v1/token' or state['limited']:
                    return None
                state['limited'] = True
                return HttpResponse(429, {'retry-after': retry_after}, b'{"error":"temporarily_unavailable"}')
            return FakeRelay(self.key, override)

        # A 429 with Retry-After of at most 60 s is waited out once, then the request succeeds with a new assertion.
        waits, relay = [], limited_once('60')
        access_token(ORIGIN, self.key, ACCOUNT_ID, HelperOptions(transport=relay, sleep=waits.append))
        self.assertEqual(waits, [60])
        self.assertEqual([status for _, _, status, _ in relay.calls], [429, 200])
        first, second = (segments(parse_qs(body.decode())['client_assertion'][0])[1]['jti'] for _, _, _, body in relay.calls)
        self.assertNotEqual(first, second)
        # A longer Retry-After is not waited: the typed error carries it for the caller.
        long_waits, long_relay = [], limited_once('61')
        with self.assertRaises(RateLimitedError) as caught:
            access_token(ORIGIN, self.key, ACCOUNT_ID, HelperOptions(transport=long_relay, sleep=long_waits.append))
        error = caught.exception
        self.assertEqual((error.status, error.retry_after, error.code), (429, 61, 'temporarily_unavailable'))
        self.assertIn('retry after 61 s', str(error))
        self.assertEqual([status for _, _, status, _ in long_relay.calls], [429])
        self.assertEqual(long_waits, [])
        # A second 429 after the one wait is reported, not waited again.
        always, always_waits = FakeRelay(self.key, lambda request, path: HttpResponse(429, {'retry-after': '5'}, b'{}')), []
        with self.assertRaises(RateLimitedError) as caught:
            access_token(ORIGIN, self.key, ACCOUNT_ID, HelperOptions(transport=always, sleep=always_waits.append))
        self.assertEqual((always_waits, caught.exception.retry_after, len(always.calls)), ([5], 5, 2))
        # A 429 without a usable Retry-After is an ordinary refusal.
        for value in ('', 'soon', '1234567', '１'):
            bare = FakeRelay(self.key, lambda request, path, value=value: HttpResponse(429, {'retry-after': value} if value else {}, b'{}'))
            with self.subTest(value), self.assertRaises(WeaveHttpError) as refused:
                access_token(ORIGIN, self.key, ACCOUNT_ID, HelperOptions(transport=bare, sleep=always_waits.append))
            self.assertNotIsInstance(refused.exception, RateLimitedError)
            self.assertEqual(len(bare.calls), 1)

    def test_helper_malformed_token_refused_without_echo(self):
        for bad in ('line\r\nInjected: header', 'trailing\n', 'has space', 'é' * 10, 'A' * 4097, '=leading', ''):
            issuing = FakeRelay(self.key, lambda request, path, bad=bad: ok({'access_token': bad, 'token_type': 'DPoP', 'expires_in': 900}) if path == '/v1/token' else None)
            with self.subTest(repr(bad[:20])):
                with self.assertRaises(WeaveHttpError) as caught:
                    access_token(ORIGIN, self.key, ACCOUNT_ID, HelperOptions(transport=issuing))
                self.assertEqual(caught.exception.code, 'unexpected-response')
                if bad:
                    self.assertNotIn(bad, str(caught.exception))
                relay = FakeRelay(self.key)
                with self.assertRaises(ValueError) as refused:
                    fetch_with_key(ORIGIN, self.key, AccessToken(bad, '2026-11-01T00:00:00.000Z', ACCOUNT_ID), 'GET', '/v1/keys', options=HelperOptions(transport=relay))
                if bad:
                    self.assertNotIn(bad, str(refused.exception))
                self.assertEqual(relay.calls, [])
        for issued in ({'access_token': ISSUED, 'token_type': 'Bearer', 'expires_in': 900},
                       {'access_token': ISSUED, 'token_type': 'DPoP', 'expires_in': 0},
                       {'access_token': ISSUED, 'token_type': 'DPoP', 'expires_in': True}):
            issuing = FakeRelay(self.key, lambda request, path, issued=issued: ok(issued))
            with self.subTest(issued), self.assertRaises(WeaveHttpError):
                access_token(ORIGIN, self.key, ACCOUNT_ID, HelperOptions(transport=issuing))
        infinite = FakeRelay(self.key, lambda request, path: HttpResponse(200, {}, b'{"access_token":"' + ISSUED.encode() + b'","token_type":"DPoP","expires_in":Infinity}'))
        with self.assertRaises(WeaveHttpError):
            access_token(ORIGIN, self.key, ACCOUNT_ID, HelperOptions(transport=infinite))

    def test_service_origin_insecure_or_not_an_origin_refused(self):
        self.assertEqual(service_origin('https://Relay.Example:443/'), 'https://relay.example')
        self.assertEqual(service_origin('https://relay.example:8443'), 'https://relay.example:8443')
        self.assertEqual(service_origin('http://127.0.0.1:9400'), 'http://127.0.0.1:9400')
        self.assertEqual(service_origin('http://[::1]:9400'), 'http://[::1]:9400')
        for bad in ('ftp://example.test', 'http://example.test', 'https://user:pw@relay.example', 'https://relay.example/base',
                    'https://relay.example/?q', 'https://relay.example/#f', 'relay.example', 'https://relay.example:99999'):
            with self.subTest(bad), self.assertRaises(ValueError):
                service_origin(bad)
        relay = FakeRelay(self.key)
        token = AccessToken(ISSUED, '2026-11-01T00:00:00.000Z', ACCOUNT_ID)
        for path in ('v1/keys', '//evil.example/v1/keys', '/v1/../v1/keys', '/v1/a b', '/v1/keys?q=a b'):
            with self.subTest(path), self.assertRaises(ValueError):
                fetch_with_key('https://relay.example', self.key, token, 'GET', path, options=HelperOptions(transport=relay))
        self.assertEqual(relay.calls, [])

    def test_helper_expires_at_is_issue_time_plus_lifetime(self):
        relay = FakeRelay(self.key)
        issued = access_token(ORIGIN, self.key, ACCOUNT_ID, HelperOptions(transport=relay, now=lambda: 1793491200.9))
        self.assertEqual(issued.expires_at, '2026-11-01T00:15:00.000Z')


if __name__ == '__main__':
    unittest.main()
