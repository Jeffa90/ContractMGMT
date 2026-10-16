#!/usr/bin/env python3
"""Repository consistency checker (task T0.5): CSV/JSON well-formedness, links and backticked paths, ID cross references,
permission/role/lookup consistency, sample-data consistency, package manifest, skills and Mermaid diagrams.

Usage: python3 -I tools/check-repo.py [repo-root]
Generated and local directories (bin/, obj/, TestResults/, coverage/, node_modules/, .ecp-dev/, .git/) are excluded.
Mermaid: blocks are always checked structurally; if ECP_MMDC (path to mermaid-cli `mmdc`) and ECP_MMDC_CONFIG are set they are also
rendered, otherwise the render step is reported NOT VERIFIED (never passed)."""
import csv, json, re, sys, os, glob
ROOT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')
os.chdir(ROOT)
problems = []
def bad(msg): problems.append(msg)

files = []
EXCLUDED = {'.git', 'bin', 'obj', 'TestResults', 'coverage', 'node_modules', '.ecp-dev', 'publish', '__pycache__', 'artifacts'}
for d, ds, fs in os.walk('.'):
    ds[:] = [x for x in ds if x not in EXCLUDED]
    for f in fs: files.append(os.path.normpath(os.path.join(d, f)))
md = [p for p in files if p.endswith('.md')]
csvs = [p for p in files if p.endswith('.csv')]
jsons = [p for p in files if p.endswith('.json')]

# 1. CSV well-formedness
for p in csvs:
    with open(p, encoding='utf-8-sig', newline='') as f:
        rows = list(csv.reader(f))
    n = len(rows[0])
    for i, r in enumerate(rows[1:], 2):
        if len(r) != n:
            bad(f'CSV {p}:{i} has {len(r)} cols, header has {n}: {r[:3]}')
# 2. JSON validity
for p in jsons:
    try: json.load(open(p, encoding='utf-8-sig'))
    except Exception as e: bad(f'JSON {p}: {e}')

# 3. Links and backticked paths
PLANNED = ('templates/packs', 'tests/Ecp.E2E.Tests', 'tests/Ecp.Adapters.ContractTests', 'tests/Ecp.Deployment.Tests', 'tests/Ecp.Performance.Tests',
           'secrets/', 'bin/', 'obj/')
def exists_loose(path):
    return os.path.exists(path)
for p in md:
    text = open(p, encoding='utf-8').read()
    base = os.path.dirname(p)
    for m in re.finditer(r'\[[^\]]*\]\(([^)#\s]+)(#[^)]*)?\)', text):
        t = m.group(1)
        if t in ('.json','.md','.csv','n/a') or t.startswith(('/var/run','packages.microsoft.com','/v2/','../../etc','DEMO-AGR')): continue
        if t.startswith(('http', 'mailto')): continue
        if not os.path.exists(os.path.normpath(os.path.join(base, t))):
            bad(f'LINK {p}: {t}')
    for m in re.finditer(r'`([^`\n]+)`', text):
        t = m.group(1).strip()
        if not re.fullmatch(r'[A-Za-z0-9_.\-/*<>{}]+', t): continue
        if '/' not in t and not re.search(r'\.(md|csv|json|yml|yaml)$', t): continue
        if re.search(r'[<>{}*]', t): continue
        if t in ('.json','.md','.csv','n/a') or t.startswith(('/var/run','packages.microsoft.com','/v2/','../../etc','DEMO-AGR')): continue
        if t.startswith(('http', '/api', '/health', '/setup', 'api/', 'assets/', 'definitions/', 'seed/', 'docs/README', 'docs/CHANGELOG', 'manifest.json', 'signature.json', 'checksums')): continue
        if t.startswith(('t/', 'a/', 'x')) : continue
        if t.startswith('/'): continue   # a URL route (for example /Setup), never a repository path
        if re.match(r'^(contracts|documents|audit|migration|config|identity|integration|notify|retention|reporting|setup|tenancy)\.', t): continue
        if any(t.startswith(x) or x in t for x in PLANNED): continue
        cands = [t, os.path.join(base, t)]
        # allow shorthand file names living in same area
        if not any(os.path.exists(c) for c in cands):
            # allow bare filename that exists somewhere in repo
            bn = os.path.basename(t)
            if '/' not in t and any(f.endswith('/' + bn) or f == bn for f in files):
                continue
            bad(f'PATH {p}: `{t}`')

# 4. ID cross references
defs = set()
pat_row = re.compile(r'^\|\s*(\*\*)?((?:GAP|CON|RISK|ASM|Q|PA|T|INV|ARC|TEN|AUT|MIG|WFL|APR|NCD|PKG|IMP|CNC|NTF|A11Y|AUD|ADP|RET|BRS|UPG|DEP|PER|SEC|PRT)-\d+(?:[A-Z])?)(\*\*)?\s*\|', re.M)
for p in md:
    text = open(p, encoding='utf-8').read()
    for m in pat_row.finditer(text): defs.add(m.group(2))
# ASM/Q ids defined in review tables: (also "| T0.1" etc. ignore)
refs = {}
pat_ref = re.compile(r'(?<![A-Za-z]-)\b((?:GAP|CON|RISK|ASM|Q|PA|INV|ARC|TEN|AUT|MIG|WFL|APR|NCD|PKG|IMP|CNC|NTF|A11Y|AUD|ADP|RET|BRS|UPG|DEP|PER|SEC|PRT)-\d+)\b|\bT-(\d{3})\b')
for p in md + csvs:
    text = open(p, encoding='utf-8-sig').read()
    for m in pat_ref.finditer(text):
        i = m.group(1) or 'T-' + m.group(2)
        refs.setdefault(i, set()).add(p)
# MIG-E/F/W codes and A-01 style are different patterns; skip
missing = sorted(i for i in refs if i not in defs)
for i in missing: bad(f'ID referenced but not defined: {i}  (in {sorted(refs[i])[:3]})')

# 5. Semantic consistency
def load(p):
    with open(p, encoding='utf-8-sig', newline='') as f: return list(csv.DictReader(f))
cat = {r['Code'] for r in load('requirements/02-reference/permission-catalogue.csv')}
mat = load('requirements/02-reference/role-permission-matrix.csv')
matp = {r['Permission'] for r in mat}
for x in sorted(cat - matp): bad(f'permission in catalogue but not matrix: {x}')
for x in sorted(matp - cat): bad(f'permission in matrix but not catalogue: {x}')
trans = load('requirements/04-workflows/status-transitions.csv') + load('requirements/04-workflows/status-transitions-supplemental.csv')
for r in trans:
    if r['Permission'] not in cat: bad(f"transition permission not in catalogue: {r['Permission']} ({r['From']}->{r['To']})")
guards = {r['GuardCode'] for r in load('requirements/04-workflows/guard-vocabulary.csv')}
for r in load('requirements/04-workflows/status-transitions-supplemental.csv'):
    for tok in re.split(r';\s*', r['Condition']):
        tok = tok.strip()
        if tok and re.fullmatch(r'[A-Z][A-Za-z]+', tok) and tok not in guards: bad(f'guard not in vocabulary: {tok}')
roles_csv = [r['Role'] for r in load('requirements/05-security/roles-and-permissions.csv')]
cols = {c.replace(' ', '').replace('/', '') for c in mat[0].keys()}
for role in roles_csv:
    key = role.replace(' ', '').replace('/', '')
    if key not in cols: bad(f'role without matrix column: {role}')
# states in transitions vs lookup
look = load('requirements/02-reference/reference-lookups.csv')
names = {r['DisplayName'] for r in look if r['LookupCode'] == 'ContractStatus'}
for r in trans:
    for s in (r['From'], r['To']):
        if s not in names: bad(f'state not in ContractStatus lookup: {s}')
lk_roles={r['DisplayName'] for r in look if r['LookupCode']=='Role'}
if lk_roles!=set(roles_csv): bad(f'Role lookup != roles csv: {lk_roles ^ set(roles_csv)}')
if len(roles_csv)!=12: bad(f'expected 12 roles, got {len(roles_csv)}')
skills=sorted(glob.glob('.claude/skills/*/SKILL.md'))
if len(skills)!=10: bad(f'expected 10 skills, got {len(skills)}')
for sk in skills:
    txt=open(sk).read()
    if not txt.startswith('---\nname: '+sk.split('/')[2]+'\n'): bad(f'skill frontmatter name mismatch {sk}')
# mapping profile covers every import column and targets valid
imp_cols = open('migration-samples/contracts-import.csv', encoding='utf-8-sig').readline().strip().split(',')
mp = load('migration-samples/field-mapping-profile-complete.csv')
mapped = {r['SourceColumn'] for r in mp}
for c in imp_cols:
    if c not in mapped: bad(f'import column unmapped in complete profile: {c}')
core = set()
for p in ['requirements/03-data-dictionary/contracts.csv', 'requirements/03-data-dictionary/contracts-extended.csv']:
    for r in load(p): core.add(r['FieldName'])
for r in mp:
    t = r['TargetField']
    if t.startswith(('supplier.', 'category.', 'subcategory.', 'staging.', 'custom.')): continue
    if t not in core: bad(f'mapping target not in dictionary: {t}')
# lookup map keys used in profile exist
lm = load('migration-samples/status-lookup-map.csv')
keys = {r['LookupMapKey'] for r in lm}
for r in mp:
    if r['LookupMapKey'] and r['LookupMapKey'] not in keys: bad(f"lookup map key missing: {r['LookupMapKey']}")
# every status/ext/category value in sample import appears in lookup map
imp = load('migration-samples/contracts-import.csv')
for row in imp:
    for col, key in (('Status', 'status'), ('ExtensionType', 'extensionType'), ('Category', 'category'), ('Subcategory', 'category')):
        v = row[col]
        if v and (key, v) not in {(r['LookupMapKey'], r['SourceValue']) for r in lm}: bad(f'sample value not in lookup map: {col}={v}')
# sample JSON consistency
users = json.load(open('sample-data/demo-users-and-roles.json'))
emails = {u['email'] for u in users['users']}
role_names = set(roles_csv)
for u in users['users'] + users['installationUsers']:
    for ro in u['roles']:
        if ro['role'] not in role_names: bad(f"user role unknown: {ro['role']}")
ca = json.load(open('sample-data/demo-categories-and-assignments.json'))
for a in ca['assignments']:
    if a['user'] not in emails: bad(f"assignment user unknown: {a['user']}")
org = json.load(open('sample-data/demo-organisation-structure.json'))
orgkeys = {o['key'] for t in org['tenants'] for o in t['organisations']}
tkeys = {t['key'] for t in org['tenants']}
for a in ca['categories'] + ca['assignments']:
    if a['organisation'] not in orgkeys or a['tenant'] not in tkeys: bad(f'unknown tenant/org in categories: {a}')
for u in users['users']:
    if u['tenant'] not in tkeys: bad(f"user tenant unknown {u['email']}")
    for ro in u['roles']:
        for o in ro.get('organisations',[]):
            if o not in orgkeys: bad(f"user org unknown {u['email']}")
        if ro['role']=='Viewer' and not ro.get('viewerKind'): bad(f"viewer without kind {u['email']}")
        if ro['scopeLevel']=='Organisation' and not ro.get('organisations'): bad(f"org role without org scope {u['email']}")
        if 'classificationCeiling' not in ro and ro['scopeLevel']!='Installation': bad(f"no ceiling {u['email']}")
# manifest
listed = set(re.findall(r'`([^`]+)`', open('PACKAGE_MANIFEST.md').read()))
actual = {p for p in files}
dirs = [x for x in listed if x.endswith('/')]
globs = [x for x in listed if '*' in x]
def covered(f):
    return f in listed or any(f.startswith(d) for d in dirs)
for x in sorted(f for f in actual if not covered(f)): bad(f'file not in PACKAGE_MANIFEST: {x}')
for x in sorted(listed):
    if x.endswith('/'):
        if not os.path.isdir(x): bad(f'manifest lists missing directory: {x}')
    elif '/' in x and '*' not in x and x not in actual and not any(x.startswith(g) for g in dirs) and not x.startswith(('bin','obj')):
        bad(f'manifest lists missing file: {x}')

# 5b. Docker build inputs: every file a project embeds from outside src/ must be copied by the Dockerfile and not excluded by .dockerignore
import glob as _glob, posixpath as _pp
dockerfile = open('deploy/docker/Dockerfile', encoding='utf-8').read() if os.path.exists('deploy/docker/Dockerfile') else ''
dockerignore = [l.strip() for l in open('.dockerignore', encoding='utf-8')] if os.path.exists('.dockerignore') else []
copied = [m.group(1).rstrip('/') for m in re.finditer(r'^COPY\s+(?!--)(\S+)\s', dockerfile, re.M)]
for proj in _glob.glob('src/**/*.csproj', recursive=True):
    for inc in re.findall(r'<EmbeddedResource\s+Include="([^"]+)"', open(proj, encoding='utf-8').read()):
        target = _pp.normpath(_pp.join(_pp.dirname(proj.replace(os.sep, '/')), inc))
        if target.startswith('src/'): continue
        if not any(target == c or target.startswith(c + '/') for c in copied): bad(f'Dockerfile does not COPY embedded resource {target} (needed by {proj})')
        excluded = False
        for rule in dockerignore:
            if not rule or rule.startswith('#'): continue
            neg = rule.startswith('!'); pat = rule.lstrip('!').rstrip('/')
            import fnmatch
            if fnmatch.fnmatch(target, pat) or any(fnmatch.fnmatch('/'.join(target.split('/')[:i]), pat) for i in range(1, len(target.split('/')) + 1)):
                excluded = not neg
        if excluded: bad(f'.dockerignore excludes embedded resource {target} (needed by {proj})')

# 6. Mermaid diagrams
import subprocess, tempfile
KEYWORDS = ('graph', 'flowchart', 'sequenceDiagram', 'classDiagram', 'stateDiagram', 'stateDiagram-v2', 'erDiagram', 'gantt', 'pie', 'journey', 'mindmap', 'timeline', 'gitGraph', 'C4Context')
blocks = []
for p in md:
    for i, m in enumerate(re.finditer(r'```mermaid\n(.*?)```', open(p, encoding='utf-8').read(), re.S)):
        blocks.append((p, i, m.group(1)))
for p, i, b in blocks:
    first = next((l.strip() for l in b.splitlines() if l.strip() and not l.strip().startswith('%%')), '')
    if not first.split(' ')[0] in KEYWORDS: bad(f'MERMAID {p}#{i}: unknown diagram type: {first[:40]}')
    if not first.startswith('erDiagram') and (b.count('[') != b.count(']') or b.count('(') != b.count(')') or b.count('{') != b.count('}')): bad(f'MERMAID {p}#{i}: unbalanced brackets')
mmdc, cfg = os.environ.get('ECP_MMDC'), os.environ.get('ECP_MMDC_CONFIG')
mermaid_status = f'{len(blocks)} blocks structurally checked; render NOT VERIFIED (set ECP_MMDC and ECP_MMDC_CONFIG)'
if mmdc and cfg and blocks:
    rendered = 0
    with tempfile.TemporaryDirectory() as td:
        for p, i, b in blocks:
            src = os.path.join(td, f'd{rendered}.mmd'); open(src, 'w').write(b)
            r = subprocess.run([mmdc, '-p', cfg, '-i', src, '-o', src + '.svg'], capture_output=True, text=True, timeout=90)
            if r.returncode != 0: bad(f'MERMAID {p}#{i}: render failed: {r.stderr.strip()[:200]}')
            rendered += 1
    mermaid_status = f'{len(blocks)} blocks rendered with mermaid-cli'
print('mermaid:', mermaid_status)
print(f'checked: {len(md)} md, {len(csvs)} csv, {len(jsons)} json; defined ids: {len(defs)}; referenced ids: {len(refs)}')
if problems:
    print(f'{len(problems)} PROBLEM(S):')
    for x in problems: print(' -', x)
    sys.exit(1)
print('ALL CHECKS PASSED')
