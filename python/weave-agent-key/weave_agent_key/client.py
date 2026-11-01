"""Registration, DPoP-bound token issue and token-authenticated requests against a Weave mailbox relay (standard-library HTTP)."""
from __future__ import annotations

import hashlib
import json
import math
import re
import time
import urllib.error
import urllib.request
import uuid
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any, Callable, Mapping, NamedTuple
from urllib.parse import urlencode, urlsplit

from .key import AgentKey, base64url, sign_jws

__all__ = ['AccessToken', 'HelperOptions', 'HttpRequest', 'HttpResponse', 'KeyedResponse', 'RateLimitedError', 'Registration',
           'WeaveHttpError', 'access_token', 'dpop_proof', 'fetch_with_key', 'register', 'service_origin']


@dataclass(frozen=True)
class HttpRequest:
    """A request for the transport. Its headers carry the access token and DPoP proof, so neither headers nor body are in its repr."""
    method: str
    url: str
    headers: Mapping[str, str] = field(repr=False)
    body: bytes | None = field(default=None, repr=False)


@dataclass(frozen=True)
class HttpResponse:
    """A relay response; header names are lower-case and repeated fields are joined with ", " as by fetch."""
    status: int
    headers: Mapping[str, str]
    body: bytes = field(default=b'', repr=False)

    def __post_init__(self) -> None:
        object.__setattr__(self, 'headers', {name.lower(): value for name, value in self.headers.items()})

    @property
    def ok(self) -> bool:
        return 200 <= self.status <= 299

    def json(self) -> Any:
        return json.loads(self.body.decode('utf-8'))


Transport = Callable[[HttpRequest], HttpResponse]


@dataclass(frozen=True)
class HelperOptions:
    """Injection points for tests and embedding hosts; the defaults are urllib, the system clock (seconds) and time.sleep (seconds)."""
    transport: Transport | None = None
    now: Callable[[], float] | None = None
    sleep: Callable[[float], None] | None = None
    timeout: float = 30.0


@dataclass(frozen=True)
class AccessToken:
    """A DPoP-bound access token and the agent it was issued to; `expires_at` is an ISO 8601 instant by the local clock."""
    token: str = field(repr=False)
    expires_at: str
    account_id: str


@dataclass(frozen=True)
class Registration:
    account_id: str


class KeyedResponse(NamedTuple):
    """The response and the token to keep using (a renewed one when the first answered 401)."""
    response: HttpResponse
    token: AccessToken


class WeaveHttpError(Exception):
    """A refused or failed relay call. `code` is the OAuth `error` or problem `code`; the response body is not retained."""

    def __init__(self, operation: str, status: int, code: str | None, detail: str = ''):
        super().__init__(f"{operation} answered HTTP {status}{'' if code is None else f' {code}'}{detail}")
        self.status = status
        self.code = code


class RateLimitedError(WeaveHttpError):
    """A 429 that was not (or no longer) waited out; `retry_after` is the relay's Retry-After in seconds."""

    def __init__(self, operation: str, code: str | None, retry_after: int):
        super().__init__(operation, 429, code, f'; retry after {retry_after} s')
        self.retry_after = retry_after


_ASSERTION_TYPE = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'
_ASSERTION_SECONDS = 60
_MAX_RETRY_WAIT_SECONDS = 60
# RFC 6750 b64token, bounded: a token is only ever a header value, never error text.
_B64TOKEN = re.compile(r'[A-Za-z0-9\-._~+/]+=*')
_MAX_TOKEN_LENGTH = 4096
_RETRY_AFTER = re.compile(r'[0-9]{1,6}')
# A relay-supplied code goes into error text: bounded, and without control or markup characters.
_SAFE_CODE = re.compile(r'[A-Za-z0-9._ -]{1,64}')
_UNSAFE_CODE = 'unexpected-error-code'
_LOOPBACK = {'localhost', '127.0.0.1', '::1'}
_DEFAULT_PORTS = {'https': 443, 'http': 80}
# These requests authenticate with a DPoP proof alone; a 401 there is never an expired token.
_KEY_ONLY_PATHS = {'/v1/accounts', '/v1/token', '/v1/key-requests'}
# Path and query characters a URL parser leaves unchanged (RFC 3986 pchar plus "/" and "?"), so the proof's htu is the URL the relay sees.
_PATH_AND_QUERY = re.compile(r"/(?:[A-Za-z0-9\-._~!$&'()*+,;=:@/?]|%[0-9A-Fa-f]{2})*")


def _usable_token(token: Any) -> bool:
    return isinstance(token, str) and len(token) <= _MAX_TOKEN_LENGTH and _B64TOKEN.fullmatch(token) is not None


def service_origin(origin: str) -> str:
    """The service origin: HTTPS, or HTTP on loopback only, with no path, query, fragment or credentials."""
    refused = ValueError('The origin must be an https:// origin (http:// only on loopback) without path, query or credentials.')
    try:
        parts = urlsplit(origin)
        port = parts.port
    except ValueError:
        raise refused from None
    host = parts.hostname
    scheme = parts.scheme.lower()
    secure = scheme == 'https' or scheme == 'http' and host in _LOOPBACK
    if not host or not host.isascii() or not secure or parts.username is not None or parts.password is not None or parts.path not in ('', '/') \
            or '?' in origin or '#' in origin:
        raise refused
    netloc = f'[{host}]' if ':' in host else host
    if port is not None and port != _DEFAULT_PORTS[scheme]:
        netloc += f':{port}'
    return f'{scheme}://{netloc}'


def _target(origin: str, path: str) -> tuple[str, str]:
    """The request URL (fragment removed) and its RFC 9449 htu (origin plus path, no query)."""
    base = service_origin(origin)
    if not path.startswith('/') or path.startswith('//'):
        raise ValueError('The path must start with a single "/".')
    path = path.split('#', 1)[0]
    pathname = path.split('?', 1)[0]
    if not _PATH_AND_QUERY.fullmatch(path) or any(segment in ('.', '..') for segment in pathname.split('/')):
        raise ValueError('The path must be an already-normalised URL path without dot segments or characters needing encoding.')
    return base + path, base + pathname


def _htu(url: str) -> str:
    parts = urlsplit(url)
    return service_origin(f'{parts.scheme}://{parts.netloc}') + (parts.path or '/')


def _seconds(options: HelperOptions | None) -> int:
    return math.floor((options.now if options and options.now else time.time)())


def dpop_proof(key: AgentKey, method: str, url: str, access_token: str | None = None, options: HelperOptions | None = None) -> str:
    """An RFC 9449 proof: `htu` is the URL without query or fragment, `htm` the uppercase method, `ath` the token hash when one is sent."""
    payload: dict[str, Any] = {'jti': str(uuid.uuid4()), 'htm': method.upper(), 'htu': _htu(url), 'iat': _seconds(options)}
    if access_token is not None:
        payload['ath'] = base64url(hashlib.sha256(access_token.encode('utf-8')).digest())
    return sign_jws(key, {'typ': 'dpop+jwt', 'jwk': dict(key.public_jwk)}, payload)


def _error_code(response: HttpResponse) -> str | None:
    try:
        body = response.json()
    except ValueError:
        return None
    if not isinstance(body, dict):
        return None
    error, code = body.get('error'), body.get('code')
    value = error if isinstance(error, str) else code if isinstance(code, str) else None
    return value if value is None or _SAFE_CODE.fullmatch(value) else _UNSAFE_CODE


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


# No environment proxies: the token and proof go only to the relay origin, never through an http_proxy/https_proxy.
_opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), _NoRedirect)


def _urllib_transport(timeout: float) -> Transport:
    def send(request: HttpRequest) -> HttpResponse:
        prepared = urllib.request.Request(request.url, data=request.body, headers=dict(request.headers), method=request.method)
        try:
            with _opener.open(prepared, timeout=timeout) as response:
                return HttpResponse(response.status, _headers(response.headers), response.read())
        except urllib.error.HTTPError as error:
            with error:
                return HttpResponse(error.code, _headers(error.headers), error.read())
    return send


def _headers(message: Any) -> dict[str, str]:
    joined: dict[str, str] = {}
    for name, value in message.items():
        name = name.lower()
        joined[name] = f'{joined[name]}, {value}' if name in joined else value
    return joined


def _send(options: HelperOptions | None, method: str, url: str, headers: Mapping[str, str], body: bytes | None) -> HttpResponse:
    # Signed requests never follow redirects: a proof is bound to the URL it names.
    transport = options.transport if options and options.transport else _urllib_transport(options.timeout if options else 30.0)
    return transport(HttpRequest(method, url, dict(headers), body))


def _json_body(value: Any) -> bytes:
    return json.dumps(value, separators=(',', ':'), ensure_ascii=False).encode('utf-8')


def register(origin: str, key: AgentKey, display_name: str | None = None, options: HelperOptions | None = None) -> Registration:
    """POST /v1/accounts with a DPoP proof by the key. Repeating it with a live key returns the same agent. A 401 is reported, never retried."""
    url, _ = _target(origin, '/v1/accounts')
    body = {} if display_name is None else {'displayName': display_name}
    response = _send(options, 'POST', url, {'Content-Type': 'application/json', 'DPoP': dpop_proof(key, 'POST', url, None, options)}, _json_body(body))
    if not response.ok:
        raise WeaveHttpError('Registration', response.status, _error_code(response))
    try:
        result = response.json()
    except ValueError:
        result = None
    if not isinstance(result, dict) or not isinstance(result.get('accountId'), str) or result.get('kid') != key.kid:
        raise WeaveHttpError('Registration', response.status, 'unexpected-response')
    return Registration(result['accountId'])


def _expires_at(iat: int, expires_in: Any) -> str | None:
    if isinstance(expires_in, bool) or not isinstance(expires_in, (int, float)) or not math.isfinite(expires_in) or not expires_in > 0:
        return None
    milliseconds = math.floor(iat * 1000 + expires_in * 1000)
    try:
        instant = datetime.fromtimestamp(milliseconds // 1000, timezone.utc)
    except (OverflowError, OSError, ValueError):
        return None
    if instant.year > 9999:
        return None
    return f"{instant.strftime('%Y-%m-%dT%H:%M:%S')}.{milliseconds % 1000:03d}Z"


def access_token(origin: str, key: AgentKey, account_id: str, options: HelperOptions | None = None) -> AccessToken:
    """POST /v1/token with a single-use `private_key_jwt` assertion and a DPoP proof; a 429 waits for a Retry-After of at most 60 s once."""
    url, audience = _target(origin, '/v1/token')
    attempt = 0
    while True:
        iat = _seconds(options)
        assertion = sign_jws(key, {'kid': key.kid},
                             {'iss': account_id, 'sub': account_id, 'aud': audience, 'iat': iat, 'exp': iat + _ASSERTION_SECONDS, 'jti': str(uuid.uuid4())})
        form = urlencode([('grant_type', 'client_credentials'), ('client_assertion_type', _ASSERTION_TYPE), ('client_assertion', assertion),
                          ('client_id', account_id)])
        response = _send(options, 'POST', url, {'Content-Type': 'application/x-www-form-urlencoded', 'DPoP': dpop_proof(key, 'POST', url, None, options)},
                         form.encode('ascii'))
        retry_after = response.headers.get('retry-after', '')
        if response.status == 429 and _RETRY_AFTER.fullmatch(retry_after):
            # A short wait is taken once; anything longer is the caller's decision.
            if attempt == 0 and int(retry_after) <= _MAX_RETRY_WAIT_SECONDS:
                (options.sleep if options and options.sleep else time.sleep)(int(retry_after))
                attempt += 1
                continue
            raise RateLimitedError('Token request', _error_code(response), int(retry_after))
        if not response.ok:
            raise WeaveHttpError('Token request', response.status, _error_code(response))
        try:
            issued = response.json()
        except ValueError:
            issued = None
        expires_at = _expires_at(iat, issued.get('expires_in')) if isinstance(issued, dict) else None
        if expires_at is None or not _usable_token(issued.get('access_token')) or not isinstance(issued.get('token_type'), str) \
                or issued['token_type'].lower() != 'dpop':
            raise WeaveHttpError('Token request', response.status, 'unexpected-response')
        return AccessToken(issued['access_token'], expires_at, account_id)


def fetch_with_key(origin: str, key: AgentKey, token: AccessToken, method: str, path: str, body: Any = None,
                   options: HelperOptions | None = None) -> KeyedResponse:
    """A token-authenticated request with `Authorization: DPoP <token>` and a fresh proof carrying `ath`. On a 401 it obtains one new
    token and repeats the request once; the token in the result is the one to keep using. `body`, when not None, is sent as JSON."""
    url, htu = _target(origin, path)
    pathname = htu[len(service_origin(origin)):]
    if pathname in _KEY_ONLY_PATHS:
        raise ValueError(f'{pathname} is not a token-authenticated request.')
    if not _usable_token(token.token):
        raise ValueError('The access token is not an RFC 6750 token.')
    verb = method.upper()
    data = None if body is None else _json_body(body)

    def call(current: AccessToken) -> HttpResponse:
        headers = {'Authorization': f'DPoP {current.token}', 'DPoP': dpop_proof(key, verb, url, current.token, options)}
        if data is not None:
            headers['Content-Type'] = 'application/json'
        return _send(options, verb, url, headers, data)

    first = call(token)
    if first.status != 401:
        return KeyedResponse(first, token)
    renewed = access_token(origin, key, token.account_id, options)
    return KeyedResponse(call(renewed), renewed)
