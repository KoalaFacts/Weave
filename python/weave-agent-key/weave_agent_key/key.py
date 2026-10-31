"""Agent key files: an Ed25519 private JWK in an owner-only file, its RFC 7638 thumbprint and compact JWS signing."""
from __future__ import annotations

import base64
import binascii
import hashlib
import json
import os
import re
import stat
from dataclasses import dataclass
from types import MappingProxyType
from typing import Any, Mapping

from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
from cryptography.hazmat.primitives.serialization import Encoding, NoEncryption, PrivateFormat, PublicFormat

__all__ = ['AgentKey', 'CreatedKey', 'KeyFileError', 'KeyFileSystem', 'create_key', 'load_key', 'sign_jws', 'thumbprint', 'write_new_key_file']

_MAX_KEY_FILE_BYTES = 4096
_PRIVATE_MEMBERS = ['crv', 'd', 'kty', 'x']
_BASE64URL = re.compile(r'[A-Za-z0-9_-]*')


class KeyFileError(Exception):
    """A key file that cannot be created or used; the message never contains key material."""


@dataclass(frozen=True)
class CreatedKey:
    kid: str


def base64url(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b'=').decode('ascii')


def _from_base64url(text: str) -> bytes | None:
    """Decodes unpadded base64url; padding, foreign characters or non-canonical spare bits are refused."""
    if not _BASE64URL.fullmatch(text) or len(text) % 4 == 1:
        return None
    try:
        data = base64.urlsafe_b64decode(text + '=' * (-len(text) % 4))
    except (binascii.Error, ValueError):
        return None
    return data if base64url(data) == text else None


def _json(value: Any) -> str:
    # The same bytes as JavaScript's JSON.stringify for the strings and integers used here.
    return json.dumps(value, separators=(',', ':'), ensure_ascii=False)


def thumbprint(jwk: Mapping[str, str]) -> str:
    """RFC 7638 thumbprint over the required Ed25519 members in lexicographic order."""
    canonical = _json({'crv': jwk['crv'], 'kty': jwk['kty'], 'x': jwk['x']})
    return base64url(hashlib.sha256(canonical.encode('utf-8')).digest())


class AgentKey:
    """A loaded agent key. The private key is held only inside `sign` and is never part of repr or attributes."""

    __slots__ = ('kid', 'public_jwk', '_sign')

    def __init__(self, private_key: Ed25519PrivateKey, x: str):
        object.__setattr__(self, 'public_jwk', MappingProxyType({'kty': 'OKP', 'crv': 'Ed25519', 'x': x}))
        object.__setattr__(self, 'kid', thumbprint(self.public_jwk))
        object.__setattr__(self, '_sign', private_key.sign)

    kid: str
    public_jwk: Mapping[str, str]

    def sign(self, data: bytes) -> bytes:
        return self._sign(data)

    def __setattr__(self, name: str, value: Any) -> None:
        raise AttributeError('AgentKey is immutable')

    def __repr__(self) -> str:
        return f'AgentKey(kid={self.kid!r})'

    def __reduce__(self):
        raise TypeError('An AgentKey cannot be serialized; keep the key file instead.')


class KeyFileSystem:
    """File operations used by `write_new_key_file`; subclassed only to inject failures in tests."""

    def open(self, path: str | os.PathLike[str]) -> int:
        return os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, 'O_BINARY', 0) | getattr(os, 'O_NOFOLLOW', 0), 0o600)

    def chmod(self, fd: int) -> None:
        # The creation mode is reduced by the umask but never widened; fchmod pins it regardless. Windows has no POSIX modes.
        if os.name != 'nt':
            os.fchmod(fd, 0o600)

    def write(self, fd: int, data: bytes) -> int:
        return os.write(fd, data)

    def fsync(self, fd: int) -> None:
        os.fsync(fd)

    def close(self, fd: int) -> None:
        os.close(fd)

    def unlink(self, path: str | os.PathLike[str]) -> None:
        os.unlink(path)


def write_new_key_file(path: str | os.PathLike[str], text: str, fs: KeyFileSystem | None = None) -> None:
    """Creates `path` exclusively with mode 0600 and writes `text`; on any failure the partial file is removed and the original error re-raised."""
    fs = fs or KeyFileSystem()
    try:
        fd = fs.open(path)
    except FileExistsError:
        raise KeyFileError(f'Key file {os.fspath(path)} already exists; it is never overwritten.') from None
    try:
        try:
            fs.chmod(fd)
            data = text.encode('utf-8')
            while data:
                data = data[fs.write(fd, data):]
            fs.fsync(fd)
        except BaseException:
            # Clean-up must not mask the write failure; a close error here adds nothing.
            try:
                fs.close(fd)
            except OSError:
                pass
            raise
        fs.close(fd)
    except BaseException:
        # Only this call created the file. An unlink failure must not replace the original error.
        try:
            fs.unlink(path)
        except OSError:
            pass
        raise


def create_key(path: str | os.PathLike[str]) -> CreatedKey:
    """Generates an Ed25519 key and writes its private JWK to a new owner-only file; an existing file is never overwritten."""
    private_key = Ed25519PrivateKey.generate()
    d = base64url(private_key.private_bytes(Encoding.Raw, PrivateFormat.Raw, NoEncryption()))
    x = base64url(private_key.public_key().public_bytes(Encoding.Raw, PublicFormat.Raw))
    write_new_key_file(path, _json({'kty': 'OKP', 'crv': 'Ed25519', 'x': x, 'd': d}) + '\n')
    return CreatedKey(thumbprint({'kty': 'OKP', 'crv': 'Ed25519', 'x': x}))


def _refuse(path: str | os.PathLike[str]) -> KeyFileError:
    return KeyFileError(f'Key file {os.fspath(path)} does not hold an Ed25519 private JWK.')


def load_key(path: str | os.PathLike[str]) -> AgentKey:
    """Reads a private JWK written by `create_key`; on POSIX a file accessible by group or others is refused."""
    fd = os.open(path, os.O_RDONLY | getattr(os, 'O_BINARY', 0))
    try:
        info = os.fstat(fd)
        if not stat.S_ISREG(info.st_mode) or info.st_size > _MAX_KEY_FILE_BYTES:
            raise _refuse(path)
        # Windows reports synthetic modes; its ACLs are not inspected here.
        if os.name != 'nt' and info.st_mode & 0o077:
            raise KeyFileError(f'Key file {os.fspath(path)} is accessible by group or others '
                               f'(mode {stat.S_IMODE(info.st_mode):o}); restrict it with chmod 600.')
        with os.fdopen(fd, 'rb', closefd=False) as file:
            raw = file.read(_MAX_KEY_FILE_BYTES + 1)
    finally:
        os.close(fd)
    try:
        value = json.loads(raw.decode('utf-8'))
    except (UnicodeDecodeError, ValueError):
        raise _refuse(path) from None
    if not isinstance(value, dict) or sorted(value) != _PRIVATE_MEMBERS or value['kty'] != 'OKP' or value['crv'] != 'Ed25519' \
            or not isinstance(value['x'], str) or not isinstance(value['d'], str):
        raise _refuse(path)
    d, x = _from_base64url(value['d']), _from_base64url(value['x'])
    if d is None or x is None or len(d) != 32 or len(x) != 32:
        raise _refuse(path)
    private_key = Ed25519PrivateKey.from_private_bytes(d)
    # A file whose x does not belong to d would sign proofs the relay rejects for a key it never registered.
    if private_key.public_key().public_bytes(Encoding.Raw, PublicFormat.Raw) != x:
        raise _refuse(path)
    return AgentKey(private_key, value['x'])


def _segment(value: Any) -> str:
    return base64url(_json(value).encode('utf-8'))


def sign_jws(key: AgentKey, header: Mapping[str, Any], payload: Mapping[str, Any]) -> str:
    """A compact JWS (RFC 7515) signed with EdDSA over `base64url(header).base64url(payload)`."""
    signing_input = f"{_segment({'alg': 'EdDSA', **header})}.{_segment(dict(payload))}"
    return f'{signing_input}.{base64url(key.sign(signing_input.encode("ascii")))}'
