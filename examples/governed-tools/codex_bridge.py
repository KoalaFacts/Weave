#!/usr/bin/env python3
"""Four MCP stdio tools over governed HTTP; the server owns persisted proposal bodies.

Run in the Agent environment without Host files or administrator secrets.
The Host MUST require approval for write_file. This adapter is not a sandbox.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import sys
from urllib import error, parse, request
import uuid

MAX_BYTES = 1_048_576
INSTRUCTIONS = ('Choose and retain one invocation_id UUID before each intended write. Pending is NOT '
                'execution success. Ask the human to review that UUID using their separate reviewer. '
                'Never approve yourself. Query get_status after interruptions. resume_write sends only '
                'the UUID after server Approved and no admitted outcome, using current authority. '
                'Never replace an unknown outcome with a new UUID. Local receipts contain no proposal body.')
FIELDS = {'read_document': ('path',), 'submit_write': ('invocation_id', 'path', 'content'),
          'get_status': ('invocation_id',), 'resume_write': ('invocation_id',)}
DESCRIPTIONS = {
    'read_document': 'Read a document from the configured Weave tool, not the local disk.',
    'submit_write': 'Create a frozen server proposal using a retained UUID. Host must require approval. Known UUIDs are queried, not automatically executed.',
    'get_status': 'Query server invocation and approval by UUID; needs no local body file. No writes.',
    'resume_write': 'After human approval, continue the server-owned original by UUID and current authority. No uploaded body or automatic retry.',
}


class BridgeError(ValueError):
    """Safe error without credentials, arbitrary response bodies or local paths."""


def encode(value):
    data = json.dumps(value, ensure_ascii=True, allow_nan=False, separators=(',', ':')).encode('utf-8')
    if len(data) > MAX_BYTES:
        raise BridgeError('Request exceeds the bridge byte limit.')
    return data


def unique(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise BridgeError('Duplicate JSON fields are not accepted.')
        result[key] = value
    return result


def invalid_constant(_):
    raise BridgeError('Invalid JSON constant.')


def load(data):
    if len(data) > MAX_BYTES:
        raise BridgeError('Response or receipt exceeds the bridge byte limit.')
    return json.loads(data, object_pairs_hook=unique, parse_constant=invalid_constant)


def component(value):
    if not isinstance(value, str) or not re.fullmatch(r'[A-Za-z0-9_-]{1,64}', value):
        raise BridgeError('Use a bounded alphanumeric identifier with hyphens or underscores.')
    return value


def identifier(value):
    if not isinstance(value, str) or not re.fullmatch(r'[0-9a-fA-F]{32}|[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}', value):
        raise BridgeError('Supply a nonzero invocation UUID, not a file name or request alias.')
    parsed = uuid.UUID(value)
    if not parsed.int:
        raise BridgeError('Supply a nonzero invocation UUID.')
    return parsed.hex


def endpoint(value):
    if not isinstance(value, str) or any(ord(c) <= 32 or ord(c) >= 127 for c in value):
        raise BridgeError('Use a configured HTTPS origin, or literal loopback HTTP.')
    parsed = parse.urlsplit(value)
    if (parsed.scheme not in ('http', 'https') or not parsed.hostname or parsed.username is not None
            or parsed.password is not None or parsed.query or parsed.fragment or parsed.path not in ('', '/')):
        raise BridgeError('Use an origin without credentials, query or path.')
    if parsed.scheme == 'http' and parsed.hostname not in ('127.0.0.1', '::1'):
        raise BridgeError('Remote Weave connections require HTTPS.')
    _ = parsed.port
    return value.rstrip('/')


class NoRedirect(request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class Bridge:
    def __init__(self, url, workspace, tool, directory, capability, bearer=None):
        self.url, self.workspace, self.tool = endpoint(url), component(workspace), component(tool)
        if not isinstance(capability, str) or not re.fullmatch(r'[A-Za-z0-9_-]{1,16384}', capability):
            raise BridgeError('An already issued Agent capability is required.')
        self.headers = {'X-Weave-Capability': capability, 'Content-Type': 'application/json', 'Accept': 'application/json'}
        if bearer:
            if len(bearer) > 16384 or any(not 33 <= ord(c) <= 126 for c in bearer):
                raise BridgeError('Invalid optional global bearer.')
            self.headers['Authorization'] = 'Bearer ' + bearer
        self.root = Path(directory)
        self.root.mkdir(mode=0o700, parents=True, exist_ok=True)
        if self.root.is_symlink():
            raise BridgeError('Receipt storage must not be a symbolic link.')
        self.save('context', {'origin': self.url, 'workspace': self.workspace, 'tool': self.tool})
        self.route = f'/api/workspaces/{self.workspace}/tools/{self.tool}/invocations'
        self.http = request.build_opener(request.ProxyHandler({}), NoRedirect())

    def read(self, key):
        path = self.root / (component(key) + '.json')
        if path.is_symlink():
            raise BridgeError('Receipts must not be symbolic links.')
        with path.open('rb') as source:
            return load(source.read(MAX_BYTES + 1))

    def save(self, key, value):
        data = encode(value)
        try:
            descriptor = os.open(self.root / (component(key) + '.json'), os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        except FileExistsError:
            if self.read(key) != value:
                raise BridgeError('Saved context or UUID input differs; it cannot be overwritten.') from None
            return
        with os.fdopen(descriptor, 'wb') as output:
            output.write(data)
            output.flush()
            os.fsync(output.fileno())

    def send(self, method, suffix='', body=None):
        try:
            message = request.Request(self.url + self.route + suffix,
                                      data=encode(body) if body is not None else None,
                                      headers=self.headers, method=method)
            try:
                response = self.http.open(message, timeout=15)
            except error.HTTPError as failure:
                response = failure
            with response:
                status = response.code
                if not 200 <= status < 300:
                    return {'http_status': status, 'result': None, 'next': 'Do not claim success or retry automatically.'}
                if response.headers.get_content_type() != 'application/json':
                    raise BridgeError('Unexpected response type; query the retained UUID, do not repeat the effect.')
                payload = load(response.read(MAX_BYTES + 1))
                if not isinstance(payload, dict):
                    raise BridgeError('Invalid response shape; query the retained UUID.')
            return {'http_status': status, 'result': payload}
        except (error.URLError, OSError, TimeoutError, ValueError, RecursionError):
            return {'http_status': None, 'result': None, 'next': 'Outcome unconfirmed. Query the retained UUID; do not create another UUID or auto-retry.'}

    def status(self, invocation_id):
        suffix = '/' + identifier(invocation_id)
        outcome = self.send('GET', suffix)
        if outcome['http_status'] != 404:
            return {'invocation': outcome, 'approval': None}
        return {'invocation': outcome, 'approval': self.send('GET', suffix + '/approval')}

    def call_tool(self, name, args):
        if name not in FIELDS or not isinstance(args, dict) or set(args) != set(FIELDS[name]):
            raise BridgeError('Unknown tool or arguments. No operator or arbitrary HTTP operations are exposed.')
        if not all(isinstance(value, str) for value in args.values()):
            raise BridgeError('Tool arguments must be strings.')
        encode(args)
        if 'path' in args and (not args['path'] or len(args['path']) > 1024 or '\x00' in args['path']):
            raise BridgeError('Supply a bounded tool-relative path; the Host enforces target permissions.')
        if name == 'read_document':
            body = {'invocationId': uuid.uuid4().hex, 'toolName': self.tool, 'method': 'read_file',
                    'parameters': {'path': args['path']}}
            return {**self.send('POST', body=body), 'invocation_id': body['invocationId']}
        invocation_id = identifier(args['invocation_id'])
        if name == 'submit_write':
            body = {'invocationId': invocation_id, 'toolName': self.tool, 'method': 'write_file',
                    'parameters': {'path': args['path']}, 'rawInput': args['content']}
            receipt = {'invocationId': invocation_id, 'inputSha256': hashlib.sha256(encode(body)).hexdigest()}
            try:
                prior = self.read(invocation_id)
            except FileNotFoundError:
                prior = None
            if prior is not None and prior != receipt:
                raise BridgeError('Changed content cannot reuse this UUID or its approval.')
            state = self.status(invocation_id)
            approval = state['approval']
            if prior is None and approval is not None and approval['http_status'] == 404:
                # No earlier local submission or server record. Retain identity/hash BEFORE POST,
                # but never make a body file necessary for subsequent review/status/continuation.
                self.save(invocation_id, receipt)
                return {**self.send('POST', body=body), 'invocation_id': invocation_id}
            return {**state, 'invocation_id': invocation_id}
        state = self.status(invocation_id)
        approval = state['approval']
        if (name == 'resume_write' and approval and approval['http_status'] == 200
                and approval['result'].get('approvalState') == 'Approved'):
            result = self.send('POST', '/' + invocation_id + '/resume')
        else:
            result = state
        return {**result, 'invocation_id': invocation_id}


def serve(bridge):
    initialized = ready = False
    while raw := sys.stdin.buffer.readline(MAX_BYTES + 1):
        reply = {'jsonrpc': '2.0', 'id': None}
        try:
            message = load(raw)
            if not isinstance(message, dict) or message.get('jsonrpc') != '2.0' or not isinstance(message.get('method'), str):
                raise BridgeError('Invalid request.')
            method = message['method']
            if 'id' not in message:
                if method == 'notifications/initialized' and initialized:
                    ready = True
                continue
            if type(message['id']) not in (str, int):
                raise BridgeError('Invalid request ID.')
            reply['id'] = message['id']
            if method == 'initialize':
                initialized = True
                result = {'protocolVersion': '2024-11-05', 'capabilities': {'tools': {}},
                          'serverInfo': {'name': 'weave-governed-files', 'version': '0.2.0'}, 'instructions': INSTRUCTIONS}
            elif method == 'ping':
                result = {}
            elif not ready:
                raise BridgeError('Complete initialization first.')
            elif method == 'tools/list':
                result = {'tools': [{'name': name, 'description': DESCRIPTIONS[name], 'inputSchema': {
                    'type': 'object', 'properties': {field: {'type': 'string'} for field in fields},
                    'required': list(fields), 'additionalProperties': False}} for name, fields in FIELDS.items()]}
            elif method == 'tools/call':
                params = message.get('params', {})
                if not isinstance(params, dict):
                    raise BridgeError('Invalid parameters.')
                try:
                    output = bridge.call_tool(params.get('name'), params.get('arguments', {}))
                    result = {'content': [{'type': 'text', 'text': json.dumps(output, ensure_ascii=True)}], 'isError': False}
                except BridgeError as failure:
                    result = {'content': [{'type': 'text', 'text': str(failure)}], 'isError': True}
                except (OSError, ValueError, RecursionError):
                    result = {'content': [{'type': 'text', 'text': 'Request not confirmed. Check UUID, arguments and current authority. Never auto-retry with a new UUID.'}], 'isError': True}
            else:
                raise BridgeError('Unsupported method.')
            reply['result'] = result
        except (BridgeError, ValueError, RecursionError, TypeError):
            reply['error'] = {'code': -32600, 'message': 'Invalid or unsupported bounded request.'}
        print(json.dumps(reply, ensure_ascii=True), flush=True)
        if len(raw) > MAX_BYTES:
            return 2
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--url', required=True)
    parser.add_argument('--workspace', required=True)
    parser.add_argument('--tool', default='files')
    parser.add_argument('--requests', type=Path, required=True, help='Private UUID/hash receipt directory; no proposal bodies')
    args = parser.parse_args()
    try:
        if any(os.environ.get(key) for key in ('WEAVE_OPERATOR_KEY', 'WEAVE_REVIEW_CAPABILITY',
                                              'Weave__Operator__Key', 'CapabilityTokens__SigningKey')):
            raise BridgeError('Remove operator, reviewer and signing secrets from the Agent environment.')
        bridge = Bridge(args.url, args.workspace, args.tool, args.requests,
                        os.environ.get('WEAVE_AGENT_CAPABILITY'), os.environ.get('WEAVE_AGENT_BEARER'))
        return serve(bridge)
    except (BridgeError, OSError, ValueError):
        print('Bridge configuration unavailable or unsafe. No operation was confirmed.', file=sys.stderr)
        return 2


if __name__ == '__main__':
    raise SystemExit(main())
