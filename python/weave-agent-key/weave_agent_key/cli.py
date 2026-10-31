"""python -m weave_agent_key: create a key, register, obtain a token and list the agent's keys. The private key never leaves the key file."""
from __future__ import annotations

import json
import sys
from typing import Any, Sequence

from .client import WeaveHttpError, access_token, fetch_with_key, register
from .key import KeyFileError, create_key, load_key

USAGE = '''Usage:
  python -m weave_agent_key init <keyfile>
  python -m weave_agent_key register <origin> <keyfile> [--name <displayName>]
  python -m weave_agent_key token <origin> <keyfile> <accountId> [--print-token]
  python -m weave_agent_key whoami <origin> <keyfile> <accountId>

The private key never leaves <keyfile>. "token" prints only the expiry unless --print-token is given.'''

_ARITY = {'init': 1, 'register': 2, 'token': 3, 'whoami': 3}


class UsageError(Exception):
    pass


def _print(value: Any) -> None:
    sys.stdout.write(json.dumps(value, separators=(',', ':'), ensure_ascii=False) + '\n')


def _parse(argv: Sequence[str]) -> tuple[list[str], str | None, bool]:
    positionals: list[str] = []
    name: str | None = None
    print_token = False
    index = 0
    while index < len(argv):
        arg = argv[index]
        if arg == '--print-token':
            print_token = True
        elif arg == '--name' or arg.startswith('--name='):
            if arg == '--name':
                index += 1
                if index == len(argv):
                    raise UsageError("Option '--name <value>' argument missing")
                name = argv[index]
            else:
                name = arg[len('--name='):]
        elif arg == '--':
            positionals.extend(argv[index + 1:])
            break
        elif arg.startswith('-') and arg != '-':
            raise UsageError(f"Unknown option '{arg.split('=', 1)[0]}'")
        else:
            positionals.append(arg)
        index += 1
    return positionals, name, print_token


def _run(argv: Sequence[str]) -> None:
    positionals, name, print_token = _parse(argv)
    if not positionals or _ARITY.get(positionals[0]) != len(positionals) - 1:
        raise UsageError('Unknown command or wrong number of arguments.')
    command, args = positionals[0], positionals[1:]
    if name is not None and command != 'register' or print_token and command != 'token':
        raise UsageError(f'An option does not apply to "{command}".')
    if command == 'init':
        return _print({'kid': create_key(args[0]).kid})
    origin, keyfile = args[0], args[1]
    key = load_key(keyfile)
    if command == 'register':
        return _print({'accountId': register(origin, key, name).account_id, 'kid': key.kid})
    account_id = args[2]
    token = access_token(origin, key, account_id)
    if command == 'token':
        return _print({'token': token.token, 'expiresAt': token.expires_at} if print_token else {'expiresAt': token.expires_at})
    response, _ = fetch_with_key(origin, key, token, 'GET', '/v1/keys')
    if not response.ok:
        raise WeaveHttpError('Key list', response.status, None)
    _print({'accountId': account_id, 'kid': key.kid, 'keys': response.json()['items']})


def main(argv: Sequence[str] | None = None) -> int:
    try:
        _run(sys.argv[1:] if argv is None else argv)
        return 0
    except UsageError as error:
        sys.stderr.write(f'weave-agent-key: {error}\n{USAGE}\n')
        return 2
    except Exception as error:  # noqa: BLE001 - the CLI reports every failure as one line and a non-zero exit
        # This package's messages never contain key material or tokens; relay response bodies are never echoed.
        known = isinstance(error, (KeyFileError, WeaveHttpError))
        sys.stderr.write(f'weave-agent-key: {error if known else f"{type(error).__name__}: {error}"}\n')
        return 1
