import json
import os
import stat
import tempfile
import threading
import unittest
from pathlib import Path

import support
from weave_agent_key import KeyFileError, create_key, load_key
from weave_agent_key.key import KeyFileSystem, write_new_key_file


class KeyTests(unittest.TestCase):
    def setUp(self):
        self._dir = tempfile.TemporaryDirectory(prefix='weave-agent-key-')
        self.dir = Path(self._dir.name)

    def tearDown(self):
        self._dir.cleanup()

    def test_create_key_new_file_writes_private_jwk_and_returns_thumbprint(self):
        path = self.dir / 'agent.jwk'
        kid = create_key(path).kid
        jwk = json.loads(path.read_text('utf-8'))
        self.assertEqual(sorted(jwk), ['crv', 'd', 'kty', 'x'])
        self.assertEqual(jwk['kty'], 'OKP')
        self.assertEqual(jwk['crv'], 'Ed25519')
        key = load_key(path)
        self.assertEqual(key.kid, kid)
        self.assertRegex(kid, r'^[A-Za-z0-9_-]{43}\Z')
        self.assertEqual(dict(key.public_jwk), {'kty': 'OKP', 'crv': 'Ed25519', 'x': jwk['x']})
        self.assertNotIn(jwk['d'], repr(key))

    def test_create_key_existing_file_refused(self):
        path = self.dir / 'agent.jwk'
        path.write_text('keep me', 'utf-8')
        with self.assertRaises(KeyFileError):
            create_key(path)
        self.assertEqual(path.read_text('utf-8'), 'keep me')
        second = self.dir / 'second.jwk'
        kid = create_key(second).kid
        with self.assertRaises(KeyFileError):
            create_key(second)
        self.assertEqual(load_key(second).kid, kid)

    @unittest.skipIf(os.name == 'nt', support.POSIX_ONLY)
    def test_create_key_posix_file_mode_is_owner_only(self):
        path = self.dir / 'agent.jwk'
        create_key(path)
        self.assertEqual(stat.S_IMODE(path.stat().st_mode), 0o600)

    @unittest.skipIf(os.name == 'nt', support.POSIX_ONLY)
    def test_load_key_world_readable_refused(self):
        path = self.dir / 'agent.jwk'
        create_key(path)
        for mode in (0o644, 0o640, 0o604, 0o620):
            os.chmod(path, mode)
            with self.assertRaisesRegex(KeyFileError, 'group or others', msg=oct(mode)):
                load_key(path)
        os.chmod(path, 0o600)
        load_key(path)

    def test_load_key_not_a_private_ed25519_jwk_refused_without_echoing_content(self):
        source = self.dir / 'agent.jwk'
        create_key(source)
        jwk = json.loads(source.read_text('utf-8'))
        other_path = self.dir / 'other.jwk'
        create_key(other_path)
        other = json.loads(other_path.read_text('utf-8'))
        cases = [
            ('public.jwk', json.dumps({'kty': 'OKP', 'crv': 'Ed25519', 'x': jwk['x']})),
            ('extra.jwk', json.dumps({**jwk, 'kid': 'x'})),
            ('mismatch.jwk', json.dumps({**jwk, 'x': other['x']})),
            ('garbage.jwk', 'not json ' + jwk['d']),
            ('padded.jwk', json.dumps({**jwk, 'd': jwk['d'] + '='})),
            ('array.jwk', json.dumps([jwk])),
        ]
        for name, content in cases:
            path = self.dir / name
            path.write_text(content, 'utf-8')
            if os.name != 'nt':
                os.chmod(path, 0o600)
            with self.subTest(name), self.assertRaises(KeyFileError) as caught:
                load_key(path)
            self.assertNotIn(jwk['d'], str(caught.exception))

    def test_create_key_write_or_close_fails_removes_partial_file_and_rethrows_original_error(self):
        class Failing(KeyFileSystem):
            def __init__(self, fail_write, fail_close, fail_unlink):
                self.fail_write, self.fail_close, self.fail_unlink = fail_write, fail_close, fail_unlink

            def write(self, fd, data):
                if self.fail_write:
                    os.write(fd, b'partial')
                    raise OSError('disk full')
                return super().write(fd, data)

            def close(self, fd):
                super().close(fd)
                if self.fail_close:
                    raise OSError('close failed')

            def unlink(self, path):
                if self.fail_unlink:
                    raise OSError('unlink failed')
                super().unlink(path)

        cases = [
            ('write.jwk', True, False, False, 'disk full'),
            ('write-and-close.jwk', True, True, False, 'disk full'),
            ('close.jwk', False, True, False, 'close failed'),
            ('unlink-fails.jwk', True, False, True, 'disk full'),
        ]
        for name, fail_write, fail_close, fail_unlink, message in cases:
            path = self.dir / name
            with self.subTest(name):
                with self.assertRaises(OSError) as caught:
                    write_new_key_file(path, '{}', Failing(fail_write, fail_close, fail_unlink))
                self.assertEqual(str(caught.exception), message)
                if fail_unlink:
                    path.unlink()
                else:
                    self.assertFalse(path.exists())

    @unittest.skipIf(os.name == 'nt', support.POSIX_ONLY)
    def test_load_key_fifo_or_device_refused_without_blocking(self):
        fifo = self.dir / 'agent.fifo'
        os.mkfifo(fifo, 0o600)
        linked = self.dir / 'linked.jwk'
        linked.symlink_to(fifo)
        for path in (fifo, linked, Path('/dev/null')):
            outcome = []

            def attempt(path=path):
                try:
                    load_key(path)
                    outcome.append('loaded')
                except KeyFileError:
                    outcome.append('refused')
            worker = threading.Thread(target=attempt, daemon=True)
            worker.start()
            worker.join(2)
            if worker.is_alive():
                # A blocked open would hold the thread forever; a writer releases it so the run can finish and report.
                os.close(os.open(fifo, os.O_WRONLY))
                worker.join(2)
                outcome.insert(0, 'blocked')
            with self.subTest(str(path)):
                self.assertEqual(outcome[:1], ['refused'])


if __name__ == '__main__':
    unittest.main()
