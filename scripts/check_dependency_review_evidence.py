#!/usr/bin/env python3
"""Fail closed on unavailable GitHub dependency comparisons; never submit snapshots.

This preflight checks availability, not complete NuGet/license coverage. The pinned
review action still owns vulnerability and license policy after this gate passes.
"""
import argparse
import base64
import binascii
import json
import os
from pathlib import Path
import re
import sys
import urllib.error
import urllib.request

MAX_PAGE_BYTES = 2 * 1024 * 1024
MAX_PAGES = 20
MAX_CHANGES = 2000
WARNING_HEADER = "x-github-dependency-graph-snapshot-warnings"


class EvidenceUnavailable(Exception):
    def __init__(self, code, warnings=()):
        super().__init__(code)
        self.code = code
        self.warnings = tuple(warnings)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


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


def inspect_comparison(repository, base_sha, head_sha, token, opener=None):
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
    for page in range(1, MAX_PAGES + 1):
        url = (f"https://api.github.com/repos/{repository}/dependency-graph/compare/"
               f"{base_sha}...{head_sha}?per_page=100&page={page}")
        request = urllib.request.Request(url, headers={
            "Accept": "application/vnd.github+json",
            "Authorization": "Bearer " + token,
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "weave-dependency-evidence"
        })
        try:
            with opener.open(request, timeout=15) as response:
                if response.status != 200:
                    raise EvidenceUnavailable("comparison-http-error")
                warning = snapshot_warning(response.headers)
                if warning is not None:
                    raise EvidenceUnavailable("missing-snapshot-evidence", (warning,))
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
            changes = json.loads(body)
        except (ValueError, UnicodeDecodeError):
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
        # The API may return a complete array larger than the requested page size.
        # Only an explicit continuation header establishes another page.
        if not next_page:
            return {"dependency_changes": total, "pages": page}
        # Never follow a response-supplied URL with the API credential.
        # Instead request the next numbered page from the same fixed endpoint.
    raise EvidenceUnavailable("comparison-pagination-limit")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args(argv)
    repository = os.environ.get("GITHUB_REPOSITORY", "")
    base_sha = os.environ.get("PR_BASE_SHA", "")
    head_sha = os.environ.get("PR_HEAD_SHA", "")
    report = {"repository": repository, "base_sha": base_sha, "head_sha": head_sha,
              "status": "unavailable"}
    try:
        report.update(inspect_comparison(repository, base_sha, head_sha,
                                         os.environ.get("GH_TOKEN", "")))
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
