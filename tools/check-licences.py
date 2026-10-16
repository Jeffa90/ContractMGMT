#!/usr/bin/env python3
"""Classify third-party package licences against tools/licence-policy.json.

Input: JSON produced by `nuget-license -o JsonPretty` (array of packages). Output: a table and an exit code.
Exit 1 if any package is Prohibited or Unknown (no recorded exception), or if an exception is listed for a package that no longer exists (stale).
Packages in `PendingOwnerReview` are reported as WARN (the build continues; release readiness must resolve them).
This is an engineering control. It is not legal advice and does not approve any licence."""
import json, sys, os

def main(licences_path, policy_path, report_path=None):
    with open(policy_path) as f: policy = json.load(f)
    allowed, restricted, prohibited = set(policy['allowed']), set(policy['restricted']), set(policy['prohibited'])
    mappings = policy.get('urlMappings', {})
    exceptions = {e['package']: e for e in policy.get('exceptions', [])}
    with open(licences_path) as f: packages = json.load(f)
    rows, errors, warnings, seen = [], [], [], set()
    for p in packages:
        pid = p['PackageId']; seen.add(pid)
        raw = p.get('License') or ''
        spdx = mappings.get(raw, raw)
        exc = exceptions.get(pid)
        if exc:
            cat = {'Recorded': 'Recorded exception', 'PendingOwnerReview': 'PENDING OWNER REVIEW'}.get(exc['status'], exc['status'])
            if exc['status'] == 'PendingOwnerReview': warnings.append(f"{pid} {p['PackageVersion']}: {exc['resolvedLicence']} ({exc['scope']})")
            rows.append((pid, p['PackageVersion'], exc['resolvedLicence'], cat))
            continue
        if spdx in prohibited: cat = 'PROHIBITED'; errors.append(f"{pid} {p['PackageVersion']}: prohibited licence {spdx}")
        elif spdx in restricted: cat = 'RESTRICTED (needs recorded review)'; errors.append(f"{pid} {p['PackageVersion']}: restricted licence {spdx} has no recorded exception")
        elif spdx in allowed: cat = 'Allowed'
        else: cat = 'UNKNOWN'; errors.append(f"{pid} {p['PackageVersion']}: licence '{raw or 'none'}' is not in the policy")
        rows.append((pid, p['PackageVersion'], spdx or raw or 'none', cat))
    for name in exceptions:
        if name not in seen: errors.append(f"stale exception: {name} is no longer a dependency")
    counts = {}
    for r in rows: counts[r[3]] = counts.get(r[3], 0) + 1
    out = ['| Category | Packages |', '|---|---|'] + [f'| {k} | {v} |' for k, v in sorted(counts.items())]
    if warnings: out += ['', 'WARN (pending owner review, not approved):'] + [f'- {w}' for w in warnings]
    if errors: out += ['', 'FAIL:'] + [f'- {e}' for e in errors]
    text = '\n'.join(out)
    print(text)
    if report_path:
        with open(report_path, 'w') as f:
            f.write('# Licence report\n\nEngineering control only; not legal advice or approval.\n\n' + text + '\n\n## All packages\n\n| Package | Version | Licence | Category |\n|---|---|---|---|\n')
            for r in sorted(rows): f.write('| %s | %s | %s | %s |\n' % r)
    return 1 if errors else 0

if __name__ == '__main__':
    sys.exit(main(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(__file__), 'licence-policy.json'), sys.argv[3] if len(sys.argv) > 3 else None))
