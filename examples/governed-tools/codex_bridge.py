#!/usr/bin/env python3
"""Four MCP stdio tools over existing governed HTTP; no approval or operator API.

Run in the Agent environment, never with the Host's files or administrator secrets.
The Host MUST require human approval for write_file. This adapter is not a sandbox.
"""
import argparse
import json
import os
from pathlib import Path
import re
import sys
from urllib import error, parse, request
import uuid

MAX_BYTES = 1_048_576
INSTRUCTIONS = ('Use one request_key per intended write and retain it. A Pending response is NOT '
                'execution success. Stop and ask the human to review the exact saved request with '
                'their separate reviewer tool. Never approve yourself. Query get_status after '
                'interruptions; resume_write only sends the original request if the server reports '
                'Approved and no admitted outcome. Never replace an unknown outcome with a new key.')
FIELDS = {'read_document': ('path',), 'submit_write': ('request_key', 'path', 'content'),
          'get_status': ('request_key',), 'resume_write': ('request_key',)}
DESCRIPTIONS = {
    'read_document': 'Read a document from the configured Weave tool, not the local disk.',
    'submit_write': 'Submit a write under Host policy; human-demo Host must require approval. Saves exact input before sending. Repeated key queries only.',
    'get_status': 'Query the saved invocation and, when not admitted, its approval. No writes.',
    'resume_write': 'After human approval, send the exact saved request with current authority. Never edit or auto-retry.',
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
        raise BridgeError('Response or saved request exceeds the bridge byte limit.')
    return json.loads(data, object_pairs_hook=unique,
                      parse_constant=invalid_constant)


def component(value):
    if not isinstance(value, str) or not re.fullmatch(r'[A-Za-z0-9_-]{1,64}', value):
        raise BridgeError('Use a bounded alphanumeric identifier with hyphens or underscores.')
    return value


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
            raise BridgeError('Request storage must not be a symbolic link.')
        self.save('context', {'origin': self.url, 'workspace': self.workspace, 'tool': self.tool})
        self.route = f'/api/workspaces/{self.workspace}/tools/{self.tool}/invocations'
        self.http = request.build_opener(request.ProxyHandler({}), NoRedirect())

    def read(self, key):
        path = self.root / (component(key) + '.json')
        if path.is_symlink():
            raise BridgeError('Saved requests must not be symbolic links.')
        with path.open('rb') as source:
            return load(source.read(MAX_BYTES + 1))

    def save(self, key, value):
        data = encode(value)
        try:
            descriptor = os.open(self.root / (component(key) + '.json'), os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        except FileExistsError:
            if self.read(key) != value:
                raise BridgeError('Saved context or request differs; it cannot be overwritten.') from None
            return
        with os.fdopen(descriptor, 'wb') as output:
            output.write(data)
            output.flush()
            os.fsync(output.fileno())
        # Saved payloads are retry evidence, not another authorization or approval store.

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
                    raise BridgeError('Unexpected response type; query the retained ID, do not repeat the effect.')
                payload = load(response.read(MAX_BYTES + 1))
                if not isinstance(payload, dict):
                    raise BridgeError('Invalid response shape; query the retained ID.')
            return {'http_status': status, 'result': payload}
        except (error.URLError, OSError, TimeoutError, ValueError, RecursionError):
            return {'http_status': None, 'result': None, 'next': 'Outcome unconfirmed. Query the retained ID; do not create another key or auto-retry.'}

    def saved(self, key):
        component(key)
        if key == 'context':
            raise BridgeError('This request key is reserved.')
        value = self.read(key)
        if (not isinstance(value, dict) or set(value) != {'invocationId', 'toolName', 'method', 'parameters', 'rawInput'}
                or not re.fullmatch(r'[0-9a-f]{32}', str(value['invocationId'])) or int(value['invocationId'], 16) == 0
                or value['toolName'] != self.tool or value['method'] != 'write_file'
                or not isinstance(value['parameters'], dict) or set(value['parameters']) != {'path'}
                or not isinstance(value['parameters']['path'], str) or not isinstance(value['rawInput'], str)):
            raise BridgeError('Saved write has an invalid shape; do not infer permission to retry.')
        return value

    def status(self, saved):
        suffix = '/' + saved['invocationId']
        outcome = self.send('GET', suffix)
        if outcome['http_status'] != 404:
            return {'invocation': outcome, 'approval': None}
        return {'invocation': outcome, 'approval': self.send('GET', suffix + '/approval')}

    def call_tool(self, name, args):
        if name not in FIELDS or not isinstance(args, dict) or set(args) != set(FIELDS[name]):
            raise BridgeError('Unknown tool or arguments. No operator or arbitrary HTTP operations are exposed.')
        if not all(isinstance(v, str) for v in args.values()):
            raise BridgeError('Tool arguments must be strings.')
        encode(args)
        if 'path' in args and (not args['path'] or len(args['path']) > 1024 or '\x00' in args['path']):
            raise BridgeError('Supply a bounded tool-relative path; the Host enforces target permissions.')
        if name == 'read_document':
            body = {'invocationId': uuid.uuid4().hex, 'toolName': self.tool, 'method': 'read_file',
                    'parameters': {'path': args['path']}}
            return {**self.send('POST', body=body), 'invocation_id': body['invocationId']}
        key = component(args['request_key'])
        if key == 'context':
            raise BridgeError('This request key is reserved.')
        if name == 'submit_write':
            try:
                saved = self.saved(key)
            except FileNotFoundError:
                saved = {'invocationId': uuid.uuid4().hex, 'toolName': self.tool, 'method': 'write_file',
                         'parameters': {'path': args['path']}, 'rawInput': args['content']}
                self.save(key, saved)  # Must finish before the first network side effect.
                return {**self.send('POST', body=saved), 'request_key': key, 'invocation_id': saved['invocationId']}
            if saved['parameters'] != {'path': args['path']} or saved['rawInput'] != args['content']:
                raise BridgeError('Changed content cannot reuse this request key or its approval.')
        else:
            saved = self.saved(key)
        state = self.status(saved)
        approval = state['approval']
        if (name == 'resume_write' and approval and approval['http_status'] == 200
                and approval['result'].get('approvalState') == 'Approved'):
            result = self.send('POST', body=saved)
        else:
            result = state
        return {**result, 'request_key': key, 'invocation_id': saved['invocationId']}


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
                          'serverInfo': {'name': 'weave-governed-files', 'version': '0.1.0'}, 'instructions': INSTRUCTIONS}
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
                    result = {'content': [{'type': 'text', 'text': 'Request not confirmed. Check arguments, saved request and current authority. Never auto-retry with a new key.'}], 'isError': True}
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
    parser.add_argument('--requests', type=Path, required=True)
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
