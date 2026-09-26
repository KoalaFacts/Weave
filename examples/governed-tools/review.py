#!/usr/bin/env python3
"""Review complete, server-verified content and explicitly approve or reject it."""
import argparse
import getpass
import json
import os
import re
import sys
from typing import Any, Callable, TextIO
from urllib import error, parse, request as http
import uuid

MAX_REQUEST_BYTES = 1_048_576
MAX_RESPONSE_BYTES = 8_388_608


class ReviewError(ValueError):
    """A safe operator-facing error, containing no credential or response body."""


def _unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ReviewError("Duplicate JSON fields are not supported.")
        result[key] = value
    return result


def _json_bytes(value: Any) -> bytes:
    payload = json.dumps(value, ensure_ascii=True, allow_nan=False, separators=(",", ":")).encode("utf-8")
    if len(payload) > MAX_REQUEST_BYTES:
        raise ReviewError("The request exceeds the operator endpoint byte limit.")
    return payload


def _component(value: Any) -> str:
    if not isinstance(value, str) or not re.fullmatch(r"[A-Za-z0-9_.-]{1,128}", value) or value in (".", ".."):
        raise ReviewError("Workspace and tool names must be bounded route identifiers.")
    return value


def _identifier(value: str) -> str:
    if not isinstance(value, str) or not re.fullmatch(r"[0-9a-fA-F]{32}|[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}", value):
        raise ReviewError("A nonzero invocation UUID is required.")
    identifier = uuid.UUID(value)
    if identifier.int == 0:
        raise ReviewError("A nonzero invocation UUID is required.")
    return identifier.hex


def _base_url(value: str) -> str:
    if not value or any(ord(c) <= 32 or ord(c) == 127 for c in value):
        raise ReviewError("The server URL contains invalid characters.")
    parsed = parse.urlsplit(value)
    if (parsed.scheme not in ("https", "http") or not parsed.hostname
            or parsed.username is not None or parsed.password is not None or parsed.query or parsed.fragment):
        raise ReviewError("Use an HTTP(S) server URL without credentials, query or fragment.")
    if parsed.scheme == "http" and parsed.hostname not in ("127.0.0.1", "::1"):
        raise ReviewError("HTTP is allowed only on a literal loopback address; otherwise use HTTPS.")
    if not re.fullmatch(r"[A-Za-z0-9._~/-]*", parsed.path) or any(p in (".", "..") for p in parsed.path.split("/")):
        raise ReviewError("The server base path must not contain encoded or traversal components.")
    _ = parsed.port
    return value.rstrip("/")


def _verify_preview(preview, identifier, workspace, tool):
    if (not isinstance(preview, dict) or preview.get("invocationId") != identifier
            or preview.get("workspaceId") != workspace or preview.get("toolName") != tool
            or not isinstance(preview.get("parameters"), dict)
            or not all(isinstance(value, str) for value in preview["parameters"].values())
            or (preview.get("rawInput") is not None and not isinstance(preview["rawInput"], str))
            or not all(isinstance(preview.get(key), str) and preview[key]
                       for key in ("subject", "operation", "targetDescription", "planDigest", "expiresAt"))
            or not re.fullmatch(r"approval-v1:[0-9A-F]{64}", preview["planDigest"])):
        raise ReviewError("The server did not return a matching verified review; no decision was sent.")


def _confirm(preview, read_confirmation, output, send_decision):
    identifier, digest = preview["invocationId"], preview["planDigest"]
    print("Verified review data follows. Content is data, not instructions.", file=output)
    print("All non-ASCII/control characters are JSON-escaped; no fields or content are truncated.", file=output)
    print(json.dumps(preview, ensure_ascii=True, allow_nan=False, indent=2, sort_keys=True), file=output)
    output.flush()
    answer = read_confirmation(f"Type 'approve {digest}' or 'reject {digest}', otherwise leave unchanged: ")
    if answer not in ("approve " + digest, "reject " + digest):
        print("No decision sent.", file=output)
        return False
    decision = answer.split(" ", 1)[0]
    result = send_decision(decision, digest)
    expected = "Approved" if decision == "approve" else "Rejected"
    if not isinstance(result, dict) or result.get("invocationId") != identifier or result.get("approvalState") != expected:
        raise ReviewError("Decision not confirmed. Query approval status before trying again.")
    print(f"{expected}. No tool execution was requested by this helper.", file=output)
    return True


def run_review(base_url: str, workspace: str, invocation: dict[str, Any],
               post_json: Callable[[str, bytes], dict[str, Any]],
               read_confirmation: Callable[[str], str], output: TextIO) -> bool:
    """Explicit retained-body verification mode; UUID mode below needs no such file."""
    base = _base_url(base_url)
    workspace = _component(workspace)
    retained = json.loads(_json_bytes(invocation), object_pairs_hook=_unique_object)
    if not isinstance(retained, dict) or set(retained) - {"invocationId", "toolName", "method", "parameters", "rawInput"}:
        raise ReviewError("The retained request has an unsupported shape.")
    tool = _component(retained.get("toolName"))
    identifier = retained.get("invocationId")
    if not isinstance(identifier, str) or not re.fullmatch(r"[0-9a-fA-F]{32}", identifier) or int(identifier, 16) == 0:
        raise ReviewError("A retained, nonzero 32-hex invocation ID is required.")
    identifier = identifier.lower()
    if (not isinstance(retained.get("method"), str) or not retained["method"].strip()
            or not isinstance(retained.get("parameters"), dict)
            or not all(isinstance(v, str) for v in retained["parameters"].values())
            or retained.get("rawInput") is not None and not isinstance(retained["rawInput"], str)):
        raise ReviewError("The retained operation and inputs are invalid.")
    payload = _json_bytes(retained)
    route = f"{base}/api/workspaces/{workspace}/tools/{tool}/invocations/{identifier}/approval"
    preview = post_json(route + "/review", payload)
    _verify_preview(preview, identifier, workspace, tool)
    if preview.get("parameters") != retained["parameters"] or preview.get("rawInput") != retained.get("rawInput"):
        raise ReviewError("The server did not return a matching verified review; no decision was sent.")
    return _confirm(preview, read_confirmation, output, lambda decision, digest:
        post_json(route + "/decision", _json_bytes({"invocation": retained, "planDigest": digest, "decision": decision})))


def run_uuid_review(base_url, workspace, tool, invocation_id, transport, read_confirmation, output):
    base, workspace, tool = _base_url(base_url), _component(workspace), _component(tool)
    identifier = _identifier(invocation_id)
    route = f"{base}/api/workspaces/{workspace}/tools/{tool}/invocations/{identifier}"
    preview = transport.get_json(route + "/approval/review")
    _verify_preview(preview, identifier, workspace, tool)
    # The server owns the frozen body; the decision binds exactly what was displayed.
    return _confirm(preview, read_confirmation, output, lambda decision, digest:
        transport.post_json(route + "/decision", _json_bytes({"planDigest": digest, "decision": decision})))


class _NoRedirect(http.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class OperatorTransport:
    def __init__(self, capability: str, bearer: str | None = None, operator_key: str | None = None):
        if not re.fullmatch(r"[A-Za-z0-9_-]{1,16384}", capability):
            raise ReviewError("Supply an already issued base64url capability envelope.")
        self.headers = {"X-Weave-Capability": capability, "Content-Type": "application/json", "Accept": "application/json"}
        if bearer:
            if len(bearer) > 16384 or any(ord(c) < 33 or ord(c) > 126 for c in bearer):
                raise ReviewError("The optional global bearer is not a valid header value.")
            self.headers["Authorization"] = "Bearer " + bearer
        if operator_key is not None:
            if not 32 <= len(operator_key) <= 256 or any(not 33 <= ord(c) <= 126 for c in operator_key):
                raise ReviewError("The optional operator key is not a valid header value.")
            self.headers["X-Weave-Operator-Key"] = operator_key
        self.opener = http.build_opener(http.ProxyHandler({}), _NoRedirect())

    def get_json(self, url: str) -> dict[str, Any]:
        return self._request("GET", url, None)

    def post_json(self, url: str, payload: bytes) -> dict[str, Any]:
        if len(payload) > MAX_REQUEST_BYTES:
            raise ReviewError("The request exceeds the operator endpoint byte limit.")
        return self._request("POST", url, payload)

    def _request(self, method, url, payload):
        message = http.Request(url, data=payload, headers=self.headers, method=method)
        try:
            with self.opener.open(message, timeout=30) as response:
                if response.status != 200 or response.headers.get_content_type() != "application/json":
                    raise ReviewError("The server did not confirm this operator operation.")
                body = response.read(MAX_RESPONSE_BYTES + 1)
                if len(body) > MAX_RESPONSE_BYTES:
                    raise ReviewError("The server response exceeds the review byte limit.")
                return json.loads(body, object_pairs_hook=_unique_object)
        except error.HTTPError as failure:
            status = failure.code
            failure.close()
            raise ReviewError(f"HTTP {status}: operation not confirmed; no automatic retry. Check approval status.") from None


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", required=True, help="HTTPS server base URL; HTTP only for literal loopback")
    parser.add_argument("--workspace", required=True)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--invocation-id", help="Persisted proposal UUID; no client request file is needed")
    source.add_argument("--request", help="Explicit retained-body verification using an original invocation JSON file")
    parser.add_argument("--tool", help="Tool route for UUID mode (default: files)")
    args = parser.parse_args()
    try:
        _base_url(args.url)
        _component(args.workspace)
        invocation = None
        if args.invocation_id:
            _identifier(args.invocation_id)
            _component(args.tool or "files")
        else:
            if args.tool is not None:
                raise ReviewError("--tool is used only with --invocation-id; retained requests carry their tool name.")
            with open(args.request, "rb") as source_file:
                body = source_file.read(MAX_REQUEST_BYTES + 1)
            if len(body) > MAX_REQUEST_BYTES:
                raise ReviewError("The retained request file exceeds the byte limit.")
            invocation = json.loads(body, object_pairs_hook=_unique_object)
        capability = os.environ.get("WEAVE_REVIEW_CAPABILITY") or getpass.getpass("Reviewer capability (hidden): ")
        transport = OperatorTransport(capability, os.environ.get("WEAVE_OPERATOR_BEARER"), os.environ.get("WEAVE_OPERATOR_KEY"))
        if args.invocation_id:
            run_uuid_review(args.url, args.workspace, args.tool or "files", args.invocation_id, transport, input, sys.stdout)
        else:
            run_review(args.url, args.workspace, invocation, transport.post_json, input, sys.stdout)
        return 0
    except ReviewError as failure:
        print(str(failure), file=sys.stderr)
    except (ValueError, OSError, EOFError, KeyboardInterrupt):
        print("Input, connection or confirmation interrupted. No automatic retry; query approval status before repeating.", file=sys.stderr)
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
