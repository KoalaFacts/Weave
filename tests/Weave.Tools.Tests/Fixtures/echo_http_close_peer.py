"""Exercise the real echo handler with its existing close path deterministically.

Only the teardown schedule and optional negative-control header removal differ.
No second request on a closing connection is dispatched or retried.
"""
import argparse
import http.server
import importlib.util
from pathlib import Path
import platform
import socket

ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location('echo_server', ROOT / 'examples/echo-mcp/server.py')
echo_server = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(echo_server)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--delay-after', type=int, choices=(200, 204), required=True)
    parser.add_argument('--implicit-close', action='store_true')
    args = parser.parse_args()

    class ScheduledCloseHandler(echo_server.McpHttpHandler):
        _status = None
        _streaming = False

        def do_POST(self):
            print('served', flush=True)
            super().do_POST()

        def send_response(self, code, message=None):
            self._status = code
            super().send_response(code, message)

        def send_header(self, keyword, value):
            if keyword.lower() == 'content-type':
                self._streaming = value == 'text/event-stream'
            if (args.implicit_close and self._status == args.delay_after
                    and keyword.lower() == 'connection' and value.lower() == 'close'):
                return
            super().send_header(keyword, value)

        def end_headers(self):
            # Isolate the chosen predecessor response, not an earlier race.
            if self._status != args.delay_after:
                self.send_header('Connection', 'close')
            super().end_headers()

        def finish(self):
            try:
                if self._status == args.delay_after and not self._streaming and self.close_connection:
                    # The real handler has finished; delay only its socket shutdown.
                    # A correct close-aware client sends EOF or opens a new socket.
                    self.connection.settimeout(2)
                    try:
                        queued = self.connection.recv(1, socket.MSG_PEEK)
                    except TimeoutError:
                        queued = b''
                    if queued:
                        # Drain the queued request so the peer produces orderly EOF,
                        # not an unread-data reset. Never dispatch this request.
                        headers = bytearray()
                        while not headers.endswith(b'\r\n\r\n'):
                            part = self.rfile.read(1)
                            if not part or len(headers) >= 8192:
                                raise RuntimeError('invalid queued test request')
                            headers.extend(part)
                        length = 0
                        for line in headers.decode('ascii').split('\r\n'):
                            if line.lower().startswith('content-length:'):
                                length = int(line.split(':', 1)[1])
                        if not 0 <= length <= 65536 or len(self.rfile.read(length)) != length:
                            raise RuntimeError('invalid queued test body')
                        print('late-post', flush=True)
            finally:
                super().finish()

    with http.server.ThreadingHTTPServer(('127.0.0.1', 0), ScheduledCloseHandler) as server:
        print('python=' + platform.python_version(), flush=True)
        print('ready=' + str(server.server_port), flush=True)
        server.serve_forever()


if __name__ == '__main__':
    main()
