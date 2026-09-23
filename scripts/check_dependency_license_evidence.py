#!/usr/bin/env python3
"""Require complete license evidence; SPDX decisions remain owned by the pinned action."""
import argparse
import json
import os
from pathlib import Path
import sys

MAX_BYTES = 2 * 1024 * 1024
MAX_CHANGES = 2000
CATEGORIES = ('unlicensed', 'unresolved', 'forbidden')


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError('duplicate field')
        result[key] = value
    return result


def inspect_licenses(payload):
    unavailable = {'status': 'unavailable', 'failure_code': 'invalid-license-evidence'}
    try:
        if not isinstance(payload, str) or len(payload.encode('utf-8')) > MAX_BYTES:
            return unavailable
        value = json.loads(payload, object_pairs_hook=unique_object)
        if not isinstance(value, dict) or set(value) != set(CATEGORIES):
            return unavailable
        counts = {}
        for category in CATEGORIES:
            entries = value[category]
            if not isinstance(entries, list) or len(entries) > MAX_CHANGES:
                return unavailable
            for entry in entries:
                if not isinstance(entry, dict) or entry.get('change_type') != 'added':
                    return unavailable
                if any(not isinstance(entry.get(key), str) or not entry[key] or len(entry[key]) > 4096
                       for key in ('name', 'version', 'manifest', 'package_url')):
                    return unavailable
            counts[category] = len(entries)
        if sum(counts.values()) > MAX_CHANGES:
            return unavailable
        if counts['forbidden']:
            return {'status': 'blocked', 'failure_code': 'license-policy-denied', **counts}
        if counts['unlicensed'] or counts['unresolved']:
            return {'status': 'unavailable', 'failure_code': 'license-evidence-unavailable', **counts}
        return {'status': 'available', **counts}
    except (ValueError, UnicodeError, TypeError, RecursionError):
        return unavailable


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args(argv)
    report = inspect_licenses(os.environ.get('INVALID_LICENSE_CHANGES', ''))
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    passed = report['status'] == 'available'
    # Do not reflect package names, raw action output or exception text into annotations.
    print('License evidence available for the reviewed changes; other policy checks remain required.' if passed
          else '::error::License evidence is incomplete or prohibited; dependency review is blocked.')
    return 0 if passed else 1


if __name__ == '__main__':
    sys.exit(main())
