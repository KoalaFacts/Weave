# weave-agent-key (Python)

A small helper for a program without a browser: it creates an Ed25519 agent key, registers the agent with a Weave mailbox relay, obtains DPoP-bound access tokens with a `private_key_jwt` assertion and calls the HTTP API. It mirrors the TypeScript helper in `typescript/agent-key/` and implements the key protocol described in `docs/implementation/2026-10-31-agent-keys.md`.

- Python 3.12 or later. The only dependency is `cryptography` (Ed25519); HTTP uses the standard library.
- The private key stays in the key file. Nothing prints, logs or returns it, and errors never echo key material, tokens or relay response bodies.
- The key file is created exclusively with mode `0600` and is never overwritten. On Linux and macOS a key file readable by group or others is refused. On Windows, file modes are not checked; protect the file with its ACL.

## Install

```bash
python -m venv .venv
. .venv/bin/activate
pip install --require-hashes -r python/weave-agent-key/requirements.txt
export PYTHONPATH=python/weave-agent-key
```

## Command line

```bash
python -m weave_agent_key init agent.jwk                               # {"kid": ...}
python -m weave_agent_key register <origin> agent.jwk --name "My agent" # {"accountId": ..., "kid": ...}
python -m weave_agent_key token <origin> agent.jwk <accountId>          # {"expiresAt": ...}
python -m weave_agent_key whoami <origin> agent.jwk <accountId>         # the agent's registered keys
```

`<origin>` is the relay origin (`https://…`, or `http://` on loopback only). `token` prints the access token only when `--print-token` is given. Exit code 2 is a usage error and 1 a refused or failed operation.

## Library

```python
from weave_agent_key import access_token, create_key, fetch_with_key, load_key, register

create_key('agent.jwk')
key = load_key('agent.jwk')
account_id = register(origin, key, 'My agent').account_id
token = access_token(origin, key, account_id)
response, token = fetch_with_key(origin, key, token, 'GET', '/v1/keys')
```

- `access_token` sends a fresh single-use assertion (`aud` is the token endpoint, lifetime 60 s) and a DPoP proof. A 429 whose `Retry-After` is at most 60 seconds is waited out once; a longer one raises `RateLimitedError` with `retry_after` for the caller to decide.
- `fetch_with_key` sends `Authorization: DPoP <token>` and a proof whose `htu` is the URL without query or fragment, `htm` the uppercase method and `ath` the token hash. On a 401 it obtains one new token and repeats the request once; keep using the token it returns. Registration and key-only requests are never retried on 401.
- Requests never follow redirects, because a proof is bound to the URL it names, and never use an environment proxy (`http_proxy`, `https_proxy`): the token and proof go only to the relay origin. `HelperOptions` replaces the transport, clock, sleep or timeout for tests and embedding hosts.
- A refused call raises `WeaveHttpError` with `status` and `code` (the relay's `error` or problem `code`, kept only when it is at most 64 characters of `A-Z a-z 0-9 . _ -` and space, otherwise `unexpected-error-code`); the response body is not retained. A success body that is not the expected JSON is `unexpected-response`.
- `load_key` opens the file non-blocking and reads only a regular file, so a FIFO, a device or a symlink to one is refused at once.
- `HttpRequest` keeps its headers and body out of `repr`, because they carry the access token and DPoP proof.

## Dependencies

`requirements.in` names the direct requirement; `requirements.txt` pins it and its transitive dependencies (`cffi`, `pycparser`) with hashes, and CI installs it with `--require-hashes`. After changing `requirements.in`, regenerate the lock in a virtual environment with pip-tools:

```bash
cd python/weave-agent-key
pip-compile --generate-hashes --no-emit-index-url --strip-extras --output-file=requirements.txt requirements.in
```

## Tests

```bash
python -m unittest discover -s python/weave-agent-key/tests -v
```

The tests skip with a message when `cryptography` is not installed. With `WEAVE_REQUIRE_CRYPTOGRAPHY=1`, as CI sets it, a missing `cryptography` fails the run instead. `tests/vectors/agent-key-vectors.json` holds wire vectors made from a test-only key that is public and must never be used elsewhere. The Python test and `typescript/agent-key/test/vectors.test.ts` both check them, and the TypeScript test also verifies the proofs with the mailbox Worker's own DPoP and JWS code.

After a deliberate wire change, regenerate the vectors from the repository root with `python python/weave-agent-key/tests/generate_vectors.py`. It reuses the committed test-only key with a fixed clock, `jti` and token; Ed25519 is deterministic, so `test_vectors.py` also checks that the script reproduces the committed file byte for byte.
