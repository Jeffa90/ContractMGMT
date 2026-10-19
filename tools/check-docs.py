#!/usr/bin/env python3
"""Implementation documentation consistency check (G-36).

The implementation guides tell people which page to open, which permission they need and what they will see. This checker holds them to the application:

  1. Every page address a guide names (for example /Admin/Operations/Email) must be a real page of the application (an @page route under src/Ecp.Host/Pages).
  2. Every permission code a guide names in code formatting must exist in the code (a string the application uses) or in the permission catalogue.
  3. Statements known to be out of date must not come back (docs/doc-consistency.json "forbidden").
  4. Statements the application now makes true must be present (docs/doc-consistency.json "required").
  5. A guide written for business roles must not tell a business administrator to run a command line: command-line tools appear only on lines that are marked as a technical task, name the technical role,
     or say what a business administrator must not do.

Usage: python3 -I tools/check-docs.py [repo-root]       Exit 0 when consistent, 1 when not (every problem printed with file and line).
"""
import json, os, re, sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))

GUIDES = [
    'docs/IMPLEMENTATION_PLAYBOOK.md', 'docs/INSTALLATION_GUIDE.md', 'docs/SETUP_WIZARD_GUIDE.md', 'docs/ORGANISATION_ONBOARDING_GUIDE.md', 'docs/BUSINESS_ADMIN_GUIDE.md',
    'docs/PACK_IMPLEMENTATION_GUIDE.md', 'docs/MIGRATION_EXECUTION_GUIDE.md', 'docs/UAT_AND_CUTOVER_GUIDE.md', 'docs/OPERATIONS_QUICK_GUIDE.md', 'docs/DOCUMENT_SCANNING.md',
    'docs/IMPLEMENTATION_REHEARSAL.md', 'docs/NO_CODE_REHEARSAL.md', 'docs/PACK_SIGNING.md', 'docs/REVERSE_PROXY.md',
]
BUSINESS_GUIDES = ['docs/BUSINESS_ADMIN_GUIDE.md', 'docs/ORGANISATION_ONBOARDING_GUIDE.md', 'docs/UAT_AND_CUTOVER_GUIDE.md']
ROUTE = re.compile(r'(?<![\w/.:-])(/(?:Admin|Onboarding|Migration|Setup|Contracts|Suppliers|Approvals|Legal|Renewals|Variations|Notifications|Dashboards|Documents)(?:/[A-Za-z0-9_{}\-<>.:]+)*)')
PERM = re.compile(r'`([a-z]+(?:\.[a-z_]+){1,3})`')
PERM_PREFIXES = ('operations.', 'migration.', 'notifications.', 'config.', 'contracts.', 'documents.', 'suppliers.', 'users.', 'roles.', 'approvals.', 'legal.', 'records.', 'audit.', 'secrets.', 'classification.', 'variations.',
                 'renewals.', 'disposition.', 'organisations.', 'categories.', 'packs.', 'terminology.', 'capabilities.', 'setup.', 'tenant.', 'identity.', 'rolemappings.')
COMMANDS = re.compile(r'`(?:ecp-pack|dotnet|docker|sqlcmd|git|powershell|bash|curl|openssl)\b|\b(?:run|execute)\s+(?:the\s+)?(?:command|script)\b', re.IGNORECASE)
TECHNICAL_MARK = re.compile(r'Technical task|System Administrator|Pack Publisher|technical role|technical step|must not|never|not a business|not for|does not need|no command', re.IGNORECASE)


def page_routes():
    routes = []
    pages = os.path.join(ROOT, 'src', 'Ecp.Host', 'Pages')
    for d, _, files in os.walk(pages):
        for f in files:
            if not f.endswith('.cshtml'): continue
            with open(os.path.join(d, f), encoding='utf-8') as fh:
                first = fh.readline()
            m = re.match(r'@page\s+"([^"]*)"', first)
            if m: routes.append(m.group(1))
    return routes


def route_regex(route):
    parts = []
    for seg in route.strip('/').split('/'):
        parts.append('[^/]+' if seg.startswith('{') else re.escape(seg))
    return re.compile('^/' + '/'.join(parts) + '/?$', re.IGNORECASE)


def known_identifiers():
    ids = set()
    for base in ('src', 'requirements'):
        for d, ds, files in os.walk(os.path.join(ROOT, base)):
            ds[:] = [x for x in ds if x not in ('bin', 'obj')]
            for f in files:
                if not f.endswith(('.cs', '.csv', '.cshtml')): continue
                try:
                    with open(os.path.join(d, f), encoding='utf-8-sig') as fh: text = fh.read()
                except OSError: continue
                ids.update(re.findall(r'[a-z]+(?:\.[a-z_]+){1,3}', text))
    return ids


def check(root=None, guides=None, config=None):
    root = root or ROOT
    problems = []
    guides = guides if guides is not None else GUIDES
    routes = [route_regex(r) for r in page_routes()]
    known = known_identifiers()
    if config is None:
        cfg_path = os.path.join(ROOT, 'docs', 'doc-consistency.json')
        config = {}
        if os.path.exists(cfg_path):
            with open(cfg_path, encoding='utf-8') as fh: config = json.load(fh)
    for rel in guides:
        path = os.path.join(root, rel)
        if not os.path.exists(path):
            problems.append(f'{rel}: the guide does not exist')
            continue
        with open(path, encoding='utf-8') as fh: text = fh.read()
        lines = text.split('\n')
        in_fence = False
        for no, line in enumerate(lines, 1):
            if line.lstrip().startswith('```'): in_fence = not in_fence
            for m in ROUTE.finditer(line):
                address = re.split(r'[?#]', m.group(1))[0].rstrip('.,;:)')
                if '<' in address or '{' in address: address = re.sub(r'/[<{][^/]*$', '/x', address)
                if not any(r.match(address) for r in routes) and not any(r.match(address + '/x') for r in routes):
                    problems.append(f'{rel}:{no}: the page address {m.group(1)} is not a page of the application')
            for m in PERM.finditer(line):
                code = m.group(1)
                if code.startswith(PERM_PREFIXES) and code not in known:
                    problems.append(f'{rel}:{no}: the permission {code} does not exist in the application')
            if rel in BUSINESS_GUIDES and COMMANDS.search(line) and not TECHNICAL_MARK.search(line):
                problems.append(f'{rel}:{no}: a command-line tool appears in a business guide without being marked as a technical task')
        for rule in config.get('forbidden', []):
            if rel in rule.get('files', guides) or '*' in rule.get('files', []):
                for no, line in enumerate(lines, 1):
                    if re.search(rule['pattern'], line):
                        problems.append(f'{rel}:{no}: out-of-date statement ({rule["why"]})')
    for rule in config.get('required', []):
        path = os.path.join(root, rule['file'])
        text = ''
        if os.path.exists(path):
            with open(path, encoding='utf-8') as fh: text = fh.read()
        if not re.search(rule['pattern'], text, re.MULTILINE | re.DOTALL):
            problems.append(f'{rule["file"]}: missing statement ({rule["why"]})')
    return problems


def main():
    global ROOT
    if len(sys.argv) > 1: ROOT = os.path.abspath(sys.argv[1])
    problems = check(root=ROOT)
    for p in problems: print('DOC', p)
    print(f'{"FAILED" if problems else "OK"}: {len(problems)} documentation problem(s) in {len(GUIDES)} guides.')
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main())
