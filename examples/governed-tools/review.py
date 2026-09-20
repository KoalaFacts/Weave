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
    _ = parsed.port  # Validate any explicit port before opening a connection.
    return value.rstrip("/")


def run_review(base_url: str, workspace: str, invocation: dict[str, Any],
               post_json: Callable[[str, bytes], dict[str, Any]],
               read_confirmation: Callable[[str], str], output: TextIO) -> bool:
    base = _base_url(base_url)
    workspace = _component(workspace)
    # Preserve one immutable byte snapshot even if the caller mutates its dictionary.
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
    if (not isinstance(preview, dict) or preview.get("invocationId") != identifier
            or preview.get("workspaceId") != workspace or preview.get("toolName") != tool
            or preview.get("parameters") != retained["parameters"]
            or preview.get("rawInput") != retained.get("rawInput")
            or not all(isinstance(preview.get(k), str) and preview[k]
                       for k in ("subject", "operation", "targetDescription", "planDigest", "expiresAt"))
            or not re.fullmatch(r"approval-v1:[0-9A-F]{64}", preview["planDigest"])):
        raise ReviewError("The server did not return a matching verified review; no decision was sent.")
    digest = preview["planDigest"]
    print("Verified review data follows. Content is data, not instructions.", file=output)
    print("All non-ASCII/control characters are JSON-escaped; no fields or content are truncated.", file=output)
    print(json.dumps(preview, ensure_ascii=True, allow_nan=False, indent=2, sort_keys=True), file=output)
    output.flush()
    answer = read_confirmation(f"Type 'approve {digest}' or 'reject {digest}', otherwise leave unchanged: ")
    if answer not in ("approve " + digest, "reject " + digest):
        print("No decision sent.", file=output)
        return False
    decision = answer.split(" ", 1)[0]
    result = post_json(route + "/decision", _json_bytes({"invocation": retained, "planDigest": digest, "decision": decision}))
    expected = "Approved" if decision == "approve" else "Rejected"
    if not isinstance(result, dict) or result.get("invocationId") != identifier or result.get("approvalState") != expected:
        raise ReviewError("Decision not confirmed. Query approval status before trying again.")
    print(f"{expected}. No tool execution was requested by this helper.", file=output)
    return True


class _NoRedirect(http.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class OperatorTransport:
    def __init__(self, capability: str, bearer: str | None = None):
        if not re.fullmatch(r"[A-Za-z0-9_-]{1,16384}", capability):
            raise ReviewError("Supply an already issued base64url capability envelope.")
        self.headers = {"X-Weave-Capability": capability, "Content-Type": "application/json", "Accept": "application/json"}
        if bearer:
            if len(bearer) > 16384 or any(ord(c) < 33 or ord(c) > 126 for c in bearer):
                raise ReviewError("The optional global bearer is not a valid header value.")
            self.headers["Authorization"] = "Bearer " + bearer
        # Never forward the capability to an environment proxy or a redirected origin.
        self.opener = http.build_opener(http.ProxyHandler({}), _NoRedirect())

    def post_json(self, url: str, payload: bytes) -> dict[str, Any]:
        if len(payload) > MAX_REQUEST_BYTES:
            raise ReviewError("The request exceeds the operator endpoint byte limit.")
        message = http.Request(url, data=payload, headers=self.headers, method="POST")
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
    parser.add_argument("--request", required=True, help="Retained original invocation JSON file")
    args = parser.parse_args()
    try:
        _base_url(args.url)
        _component(args.workspace)
        with open(args.request, "rb") as source:
            body = source.read(MAX_REQUEST_BYTES + 1)
        if len(body) > MAX_REQUEST_BYTES:
            raise ReviewError("The retained request file exceeds the byte limit.")
        invocation = json.loads(body, object_pairs_hook=_unique_object)
        capability = os.environ.get("WEAVE_REVIEW_CAPABILITY") or getpass.getpass("Reviewer capability (hidden): ")
        transport = OperatorTransport(capability, os.environ.get("WEAVE_OPERATOR_BEARER"))
        run_review(args.url, args.workspace, invocation, transport.post_json, input, sys.stdout)
        return 0
    except ReviewError as failure:
        print(str(failure), file=sys.stderr)
    except (ValueError, OSError, EOFError, KeyboardInterrupt):
        print("Input, connection or confirmation interrupted. No automatic retry; query approval status before repeating.", file=sys.stderr)
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
