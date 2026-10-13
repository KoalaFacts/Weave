"""Render a path-scoped userns prerequisite for ephemeral Ubuntu browser CI."""
import argparse
import os
from pathlib import Path
import re


def chromium_profile(executable):
    cache = Path.home() / '.cache/ms-playwright'
    expected = re.escape(str(cache)) + r'/chromium-[0-9]+/chrome-linux64/chrome'
    # AppArmor attachment syntax interprets globbing even inside quotes.
    if (not re.fullmatch(r'/[A-Za-z0-9_./-]+', executable)
            or not re.fullmatch(expected, executable)):
        raise ValueError('Expected the exact managed Chromium executable in the default Playwright cache.')
    browser = Path(executable)
    if not browser.is_file() or not os.access(browser, os.X_OK):
        raise ValueError('Install the pinned Playwright Chromium first.')
    if browser.resolve() != browser:
        raise ValueError('The managed Chromium path must not contain symlinks.')
    return ('abi <abi/4.0>,\n'
            f'profile weave-playwright-chromium "{browser}" flags=(unconfined) {{\n'
            '  userns,\n}\n')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('executable', help='The pinned SDK chromium.executablePath() result')
    arguments = parser.parse_args()
    try:
        print(chromium_profile(arguments.executable), end='')
    except ValueError as error:
        parser.exit(1, f'{error}\n')
