#!/usr/bin/env python3
"""Reject incomplete NuGet audit evidence and direct/transitive vulnerabilities."""
import argparse
import json
from pathlib import Path
import sys


def validate_report(report):
    if (not isinstance(report, dict) or type(report.get('version')) is not int
            or report['version'] != 1 or not isinstance(report.get('projects'), list)
            or not report['projects']):
        raise ValueError('Missing or unsupported dependency audit report')
    for project in report['projects']:
        if (not isinstance(project, dict) or not isinstance(project.get('path'), str)
                or not project['path'].strip()):
            raise ValueError('Missing or invalid project identity in dependency audit report')

    def check(node):
        if isinstance(node, dict):
            if node.get('vulnerabilities'):
                raise ValueError('Dependency vulnerabilities remain')
            if str(node.get('level', '')).lower() in ('error', 'warning'):
                raise ValueError('Dependency audit did not complete cleanly')
            for value in node.values():
                check(value)
        elif isinstance(node, list):
            for value in node:
                check(value)

    check(report)
    return len(report['projects'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('report', type=Path)
    args = parser.parse_args()
    try:
        count = validate_report(json.loads(args.report.read_text()))
    except (OSError, ValueError) as error:
        print(f'Dependency audit failed: {error}', file=sys.stderr)
        return 1
    print(f'No reported vulnerabilities across {count} projects.')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
