#!/usr/bin/env python3
"""Observe three fixed comparison responses; never produce review policy evidence.

The fixed pair reproduces the earlier local observation. Execution identities are
separate: this diagnostic does not inspect or approve the executing PR revision.
Only bounded hashes, counts and validated public metadata leave the process.
"""
from contextlib import contextmanager
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import time
import urllib.error
import urllib.request

from check_dependency_review_evidence import (
    MAX_CHANGES, MAX_PAGE_BYTES, NoRedirect, finite_float, reject_nonfinite, unique_object,
)

BASE = 'afcadc0922501ac822a3d1ca0e112376a890d6ac'
HEAD = 'ff4240a9e4d0803337dc90ecf472fe6a588d84c2'
ENDPOINT = f'https://api.github.com/repos/KoalaFacts/Weave/dependency-graph/compare/{BASE}...{HEAD}'
QUERIES = ('?per_page=5&page=1', '?per_page=5&page=2', '?per_page=100&page=1')
REQUEST_SECONDS = 15
TOTAL_SECONDS = 60


class DiagnosticError(Exception):
    def __init__(self, code):
        super().__init__(code)
        self.code = code


def utc_now():
    return datetime.now(timezone.utc).isoformat()


@contextmanager
def hard_timeout(seconds):
    # Linux runner alarm bounds slow reads as well as DNS/TLS/socket operations.
    def expired(signum, frame):
        raise DiagnosticError('diagnostic-timeout')

    previous = signal.signal(signal.SIGALRM, expired)
    signal.setitimer(signal.ITIMER_REAL, seconds)
    try:
        yield
    finally:
        signal.setitimer(signal.ITIMER_REAL, 0)
        signal.signal(signal.SIGALRM, previous)


def public_headers(headers):
    result = {}
    patterns = {
        'X-GitHub-Request-Id': ('request_id', r'[0-9A-Fa-f]{1,16}(?::[0-9A-Fa-f]{1,16}){1,7}'),
        'X-GitHub-Api-Version-Selected': ('api_version', r'2022-11-28'),
        'Date': ('response_date', r'(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun), [0-9]{2} '
                 r'(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) [0-9]{4} '
                 r'[0-9]{2}:[0-9]{2}:[0-9]{2} GMT'),
        'Link': ('link', None),
    }
    for header, (key, pattern) in patterns.items():
        values = headers.get_all(header, [])
        if len(values) > 1:
            raise DiagnosticError('diagnostic-invalid-header')
        value = values[0] if values else None
        if value is not None:
            if len(value) > 4096 or (pattern and not re.fullmatch(pattern, value)):
                raise DiagnosticError('diagnostic-invalid-header')
            if key == 'response_date':
                try:
                    datetime.strptime(value, '%a, %d %b %Y %H:%M:%S GMT')
                except ValueError:
                    raise DiagnosticError('diagnostic-invalid-header') from None
            if key == 'link':
                path = rf'https://api\.github\.com/(?:repos/KoalaFacts/Weave|repositories/1178623688)/dependency-graph/compare/{BASE}\.\.\.{HEAD}'
                query = r'(?:per_page=(?:5|100)&page=[1-9][0-9]{0,5}|page=[1-9][0-9]{0,5}&per_page=(?:5|100))'
                entry = rf'<{path}\?{query}>; rel="(?:next|prev|first|last)"'
                if not re.fullmatch(rf'{entry}(?:, {entry}){{0,3}}', value):
                    raise DiagnosticError('diagnostic-invalid-header')
        result[key] = value
    return result


def collect(token, opener=None):
    if not token or any(ord(char) < 33 or ord(char) > 126 for char in token):
        raise DiagnosticError('diagnostic-invalid-credential')
    if opener is None:
        # Match the existing preflight transport; no redirects or environment proxy.
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    deadline = time.monotonic() + TOTAL_SECONDS
    results = []
    total_records = 0
    for query in QUERIES:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise DiagnosticError('diagnostic-timeout')
        timeout = min(REQUEST_SECONDS, remaining)
        with hard_timeout(timeout):
            row = {'query': query, 'started_at': utc_now()}
            request = urllib.request.Request(ENDPOINT + query, headers={
                'Accept': 'application/vnd.github+json', 'Authorization': 'Bearer ' + token,
                'X-GitHub-Api-Version': '2022-11-28', 'User-Agent': 'weave-dependency-evidence',
            })
            try:
                with opener.open(request, timeout=timeout) as response:
                    if response.status != 200:
                        raise DiagnosticError('diagnostic-http-error')
                    headers = public_headers(response.headers)
                    body = response.read(MAX_PAGE_BYTES + 1)
            except urllib.error.HTTPError as error:
                error.close()
                raise DiagnosticError('diagnostic-http-error') from None
            except (urllib.error.URLError, OSError, TimeoutError):
                raise DiagnosticError('diagnostic-network-error') from None
            if len(body) > MAX_PAGE_BYTES:
                raise DiagnosticError('diagnostic-response-too-large')
            try:
                records = json.loads(body, object_pairs_hook=unique_object,
                                     parse_float=finite_float, parse_constant=reject_nonfinite)
                if (not isinstance(records, list)
                        or any(not isinstance(item, dict) or item.get('change_type') not in ('added', 'removed')
                               or not isinstance(item.get('manifest'), str) for item in records)):
                    raise ValueError()
                total_records += len(records)
                if total_records > MAX_CHANGES:
                    raise DiagnosticError('diagnostic-change-limit')
                canonical = json.dumps(sorted(json.dumps(item, sort_keys=True, separators=(',', ':'), ensure_ascii=True)
                                              for item in records), separators=(',', ':')).encode()
            except (ValueError, UnicodeDecodeError, RecursionError):
                raise DiagnosticError('diagnostic-invalid-response') from None
            row.update(headers, status=200, finished_at=utc_now(), body_bytes=len(body), records=len(records),
                       body_sha256=hashlib.sha256(body).hexdigest(),
                       canonical_multiset_sha256=hashlib.sha256(canonical).hexdigest())
            results.append(row)
    return results


def main():
    report = {'comparison_base': BASE, 'comparison_head': HEAD, 'purpose': 'observation-only'}
    try:
        for key, variable in (('execution_sha', 'GITHUB_SHA'), ('pr_head_sha', 'PR_HEAD_SHA')):
            value = os.environ.get(variable, '')
            if not re.fullmatch(r'[0-9a-f]{40}', value):
                raise DiagnosticError('diagnostic-invalid-identity')
            report[key] = value
        report['responses'] = collect(os.environ.get('GH_TOKEN', ''))
    except DiagnosticError as error:
        report['failure_code'] = error.code
    except Exception:
        # Never render exception text: transports and parsers can embed credentials/body.
        report['failure_code'] = 'diagnostic-internal-error'
    payload = json.dumps(report, sort_keys=True, separators=(',', ':'))
    # Runner command parsing, not a checks API write. JSON contains no raw response body.
    escaped = payload.replace('%', '%25').replace('\r', '%0D').replace('\n', '%0A')
    print('::notice title=Dependency comparison diagnostic::' + escaped)
    try:
        if os.environ.get('DIAGNOSTIC_REPORT'):
            Path(os.environ['DIAGNOSTIC_REPORT']).write_text(payload + '\n')
        if os.environ.get('GITHUB_STEP_SUMMARY'):
            with Path(os.environ['GITHUB_STEP_SUMMARY']).open('a') as output:
                output.write('### Fixed dependency comparison diagnostic (observation only)\n\n```json\n' + payload + '\n```\n')
    except OSError:
        print('::notice title=Dependency comparison diagnostic::diagnostic-output-error')
        return 1
    return 1 if 'failure_code' in report else 0


if __name__ == '__main__':
    raise SystemExit(main())
