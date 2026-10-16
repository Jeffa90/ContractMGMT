#!/usr/bin/env bash
# Summarise .trx results as Passed / Failed / Skipped / NOT VERIFIED. Skipped tests whose reason starts with
# "NOT VERIFIED" are counted separately and are NEVER counted as Passed.
# Usage: tools/test-report.sh [results-dir ...]   (default: TestResults)
set -euo pipefail
python3 -I - "${@:-TestResults}" <<'PY'
import sys, glob, os
import xml.etree.ElementTree as ET
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
passed = failed = skipped = notver = 0
unverified = []
files = []
for d in sys.argv[1:]:
    files += glob.glob(os.path.join(d, '**', '*.trx'), recursive=True) if os.path.isdir(d) else [d]
for f in files:
    for r in ET.parse(f).getroot().iter('{%s}UnitTestResult' % ns['t']):
        o = r.get('outcome')
        if o == 'Passed': passed += 1
        elif o == 'Failed': failed += 1
        elif o in ('NotExecuted', 'Skipped'):
            msg = ''.join(r.itertext())
            if 'NOT VERIFIED' in msg: notver += 1; unverified.append(r.get('testName'))
            else: skipped += 1
print('| Result | Count |\n|---|---|')
print(f'| Passed | {passed} |\n| Failed | {failed} |\n| Skipped | {skipped} |\n| NOT VERIFIED | {notver} |')
if notver: print('\nNOT VERIFIED (environment unavailable, not passed): ' + ', '.join(unverified[:10]) + (' ...' if len(unverified) > 10 else ''))
if not files: print('\nNo result files found.')
sys.exit(1 if failed else 0)
PY
