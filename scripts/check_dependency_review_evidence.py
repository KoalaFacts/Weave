#!/usr/bin/env python3
"""Fail closed on unavailable GitHub dependency comparisons; never submit snapshots.

This preflight checks availability, not complete NuGet/license coverage. The pinned
review action still owns vulnerability and license policy after this gate passes.
"""
import argparse
import base64
import binascii
import json
import math
import os
from pathlib import Path
import re
import sys
import time
import urllib.error
import urllib.request

MAX_PAGE_BYTES = 2 * 1024 * 1024
MAX_PAGES = 20
MAX_CHANGES = 2000
WARNING_HEADER = "x-github-dependency-graph-snapshot-warnings"
MAX_WARNING_ATTEMPTS = 61
WARNING_INTERVAL_SECONDS = 10


class EvidenceUnavailable(Exception):
    def __init__(self, code, warnings=()):
        super().__init__(code)
        self.code = code
        self.warnings = tuple(warnings)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError('duplicate field')
        result[key] = value
    return result


def finite_float(value):
    number = float(value)
    if not math.isfinite(number):
        raise ValueError('nonfinite number')
    return number


def reject_nonfinite(_):
    raise ValueError('nonfinite number')


def snapshot_warning(headers):
    encoded = headers.get(WARNING_HEADER, "")
    if not encoded:
        return None
    if len(encoded) > 16384:
        raise EvidenceUnavailable("invalid-snapshot-warning")
    try:
        warning = base64.b64decode(encoded, validate=True).decode("utf-8")
    except (ValueError, binascii.Error, UnicodeDecodeError):
        raise EvidenceUnavailable("invalid-snapshot-warning") from None
    # An unexpected nonempty warning header is not evidence of a complete review.
    if not warning.strip():
        raise EvidenceUnavailable("invalid-snapshot-warning")
    return warning


def inspect_comparison(repository, base_sha, head_sha, token, opener=None, *, deadline=None):
    if (not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository)
            or any(part in (".", "..") for part in repository.split("/"))
            or not re.fullmatch(r"[0-9a-fA-F]{40}", base_sha)
            or not re.fullmatch(r"[0-9a-fA-F]{40}", head_sha)):
        raise EvidenceUnavailable("invalid-comparison-identity")
    if not token or any(ord(char) < 33 or ord(char) > 126 for char in token):
        raise EvidenceUnavailable("missing-or-invalid-api-credential")
    if opener is None:
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    total = 0
    records = []
    warnings = []
    for page in range(1, MAX_PAGES + 1):
        timeout = 15 if deadline is None else min(15, deadline - time.monotonic())
        if timeout <= 0:
            raise EvidenceUnavailable("snapshot-evidence-timeout", warnings)
        url = (f"https://api.github.com/repos/{repository}/dependency-graph/compare/"
               f"{base_sha}...{head_sha}?per_page=100&page={page}")
        request = urllib.request.Request(url, headers={
            "Accept": "application/vnd.github+json",
            "Authorization": "Bearer " + token,
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "weave-dependency-evidence"
        })
        try:
            with opener.open(request, timeout=timeout) as response:
                if response.status != 200:
                    raise EvidenceUnavailable("comparison-http-error")
                warning = snapshot_warning(response.headers)
                if warning is not None:
                    warnings.append(warning)
                body = response.read(MAX_PAGE_BYTES + 1)
                next_page = 'rel="next"' in response.headers.get("Link", "")
        except urllib.error.HTTPError as error:
            error.close()
            raise EvidenceUnavailable("comparison-http-error") from None
        except (urllib.error.URLError, OSError, TimeoutError):
            raise EvidenceUnavailable("comparison-network-error") from None
        if len(body) > MAX_PAGE_BYTES:
            raise EvidenceUnavailable("comparison-response-too-large")
        try:
            changes = json.loads(body, object_pairs_hook=unique_object,
                                 parse_float=finite_float, parse_constant=reject_nonfinite)
        except (ValueError, UnicodeDecodeError, RecursionError):
            raise EvidenceUnavailable("invalid-comparison-response") from None
        if not isinstance(changes, list):
            raise EvidenceUnavailable("invalid-comparison-response")
        if total + len(changes) > MAX_CHANGES:
            raise EvidenceUnavailable("comparison-change-limit")
        if any(not isinstance(change, dict)
               or change.get("change_type") not in ("added", "removed")
               or not isinstance(change.get("manifest"), str) for change in changes):
            raise EvidenceUnavailable("invalid-comparison-response")
        total += len(changes)
        records.extend(changes)
        if deadline is not None and time.monotonic() >= deadline:
            raise EvidenceUnavailable("snapshot-evidence-timeout", warnings)
        # The API may return a complete array larger than the requested page size.
        # Only an explicit continuation header establishes another page.
        if not next_page:
            if warnings:
                raise EvidenceUnavailable("missing-snapshot-evidence", warnings)
            return {"dependency_changes": total, "pages": page, "dependency_records": records}
        # Never follow a response-supplied URL with the API credential.
        # Instead request the next numbered page from the same fixed endpoint.
    raise EvidenceUnavailable("comparison-pagination-limit")


def wait_for_comparison(repository, base_sha, head_sha, token, wait_seconds):
    if not wait_seconds:
        return inspect_comparison(repository, base_sha, head_sha, token)
    deadline = time.monotonic() + wait_seconds
    for attempt in range(1, MAX_WARNING_ATTEMPTS + 1):
        try:
            return inspect_comparison(repository, base_sha, head_sha, token, deadline=deadline)
        except EvidenceUnavailable as error:
            if error.code != "missing-snapshot-evidence":
                raise
            remaining = deadline - time.monotonic()
            if remaining <= 0 or attempt == MAX_WARNING_ATTEMPTS:
                raise EvidenceUnavailable("snapshot-evidence-timeout", error.warnings) from None
            time.sleep(min(WARNING_INTERVAL_SECONDS, remaining))


def wait_budget(value):
    try:
        seconds = int(value)
    except ValueError:
        raise argparse.ArgumentTypeError("snapshot wait must be an integer from 0 through 600") from None
    if not 0 <= seconds <= 600:
        raise argparse.ArgumentTypeError("snapshot wait must be an integer from 0 through 600")
    return seconds


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--snapshot-wait-seconds", type=wait_budget, default=0,
                        help="wait up to 600 seconds only for valid snapshot warnings (default: 0)")
    args = parser.parse_args(argv)
    repository = os.environ.get("GITHUB_REPOSITORY", "")
    base_sha = os.environ.get("PR_BASE_SHA", "")
    head_sha = os.environ.get("PR_HEAD_SHA", "")
    report = {"repository": repository, "base_sha": base_sha, "head_sha": head_sha,
              "status": "unavailable"}
    try:
        report.update(wait_for_comparison(repository, base_sha, head_sha,
                                          os.environ.get("GH_TOKEN", ""), args.snapshot_wait_seconds))
        report["status"] = "available"
        code = 0
    except EvidenceUnavailable as error:
        report["failure_code"] = error.code
        report["snapshot_warnings"] = list(error.warnings)
        code = 1
    args.report.write_text(json.dumps(report, ensure_ascii=True, indent=2) + "\n", encoding="utf-8")
    if code:
        print("::error::Dependency comparison evidence is unavailable. The review must not be treated as clean.")
    else:
        print(f"Dependency comparison available: {report['dependency_changes']} change(s). "
              "Zero changes is not a full dependency or license audit.")
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as output:
            output.write("## Dependency comparison evidence\n\n")
            output.write("**Unavailable — review blocked.**\n" if code else
                         "Available for the exact base/head pair; the policy review follows.\n")
            output.write("\nThis check does not establish complete NuGet dependency or license coverage. "
                         "See the dependency-review-evidence artifact for the checked SHAs and status.\n")
    return code


if __name__ == "__main__":
    sys.exit(main())
