#!/usr/bin/env python3
"""Scan the files git tracks for things that look like real secrets. Exit 1 if any are found.

This is a safety net, not a guarantee: it looks for private keys, well-known token formats, and secret-looking assignments with a real-looking value.
Synthetic values used by tests and documentation are recognised by their wording ("not-a-real-secret", "example", "synthetic", "invalid", ...) or are named,
with a reason, in tools/secrets-allowlist.json. Findings print the file, line and rule; the matched value is never printed.
Usage: check-secrets.py [repository-root]
"""
import json, os, re, subprocess, sys

PRIVATE_KEY = re.compile(r"-----BEGIN (?:RSA |EC |DSA |OPENSSH |ENCRYPTED |PGP )?PRIVATE KEY(?: BLOCK)?-----")
TOKEN_RULES = [
    ("github-token", re.compile(r"\bgh[pousr]_[A-Za-z0-9]{36,}\b")),
    ("github-fine-grained-token", re.compile(r"\bgithub_pat_[A-Za-z0-9_]{50,}\b")),
    ("aws-access-key-id", re.compile(r"\bAKIA[0-9A-Z]{16}\b")),
    ("slack-token", re.compile(r"\bxox[baprs]-[A-Za-z0-9-]{10,}\b")),
    ("google-api-key", re.compile(r"\bAIza[0-9A-Za-z_-]{35}\b")),
    ("anthropic-api-key", re.compile(r"\bsk-ant-[A-Za-z0-9_-]{20,}\b")),
    ("openai-style-key", re.compile(r"\bsk-[A-Za-z0-9]{32,}\b")),
    ("json-web-token", re.compile(r"\beyJ[A-Za-z0-9_-]{15,}\.eyJ[A-Za-z0-9_-]{15,}\.[A-Za-z0-9_-]{10,}\b")),
    ("azure-storage-key", re.compile(r"AccountKey=[A-Za-z0-9+/]{60,}={0,2}")),
]
# Secret-looking assignment with a value of 8+ characters. The value must not be a variable, a placeholder, a reference or an obviously synthetic word.
ASSIGNMENT = re.compile(r"""(?i)[A-Za-z0-9_.-]*(password|pwd|passphrase|secret|api[_-]?key|access[_-]?token|auth[_-]?token|private[_-]?key)[A-Za-z0-9_-]*["']?\s*[:=]\s*(?P<quote>["']?)(?P<value>[^\s"';,)}]{8,})""")
# In program text a secret is a quoted literal; an unquoted right-hand side is an expression (a call, a variable), not a value. Configuration files hold bare values.
CONFIG_EXTENSIONS = {".json", ".yml", ".yaml", ".config", ".env", ".ini", ".props", ".toml", ".conf", ".properties", ".example"}
SYNTHETIC = re.compile(r"(?i)(not[-_ ]a[-_ ]real|synthetic|example|invalid|dummy|fake|sample|placeholder|changeme|change[-_]me|redacted|\*\*\*|xxxx|test|demo|supersecret|hunter2|mysecret|secret123|your[-_ ]|<|>|\$\{|\$\(|\{\{|%s|\bnull\b|\bnone\b|\btrue\b|\bfalse\b|^env:|^file:|^keyvault:|^dpapi:|\.\.\.|^\$|^@|\(\)|^new\b|^string\b|^value\b|^password\b|^secret\b|^token\b|^get|^read|^resolve|^config|^options|^settings|^request|^response)")
SKIP_EXTENSIONS = {".png", ".jpg", ".jpeg", ".gif", ".ico", ".pdf", ".zip", ".ecpkg", ".dll", ".exe", ".woff", ".woff2", ".bak", ".db", ".pfx", ".snk", ".docx", ".xlsx", ".bin"}


def tracked_files(root):
    out = subprocess.run(["git", "-C", root, "ls-files", "-z"], capture_output=True, check=True).stdout.decode("utf-8", "replace")
    return [f for f in out.split("\0") if f]


def load_allowlist(root):
    path = os.path.join(root, "tools", "secrets-allowlist.json")
    if not os.path.isfile(path):
        return []
    with open(path, encoding="utf-8") as f:
        entries = json.load(f)
    for e in entries:
        if not e.get("reason"):
            raise SystemExit(f"{path}: every allowance needs a reason ({e})")
    return entries


def allowed(entries, rel, rule):
    return any((e.get("path") == rel or (e.get("path_prefix") and rel.startswith(e["path_prefix"]))) and e.get("rule") in (rule, "*") for e in entries)


def scan(root):
    entries = load_allowlist(root)
    findings = []
    for rel in tracked_files(root):
        if os.path.splitext(rel)[1].lower() in SKIP_EXTENSIONS or rel.endswith("packages.lock.json") or rel == "tools/check-secrets.py":
            continue
        full = os.path.join(root, rel)
        try:
            with open(full, "rb") as f:
                data = f.read()
        except OSError:
            continue
        if b"\0" in data[:4096]:
            continue
        text = data.decode("utf-8", "replace")
        for n, line in enumerate(text.splitlines(), 1):
            if PRIVATE_KEY.search(line) and not allowed(entries, rel, "private-key"):
                findings.append((rel, n, "private-key"))
            for rule, rx in TOKEN_RULES:
                hit = rx.search(line)
                if hit and not SYNTHETIC.search(hit.group(0)) and not allowed(entries, rel, rule):
                    findings.append((rel, n, rule))
            for m in ASSIGNMENT.finditer(line):
                value = m.group("value")
                if not m.group("quote") and os.path.splitext(rel)[1].lower() not in CONFIG_EXTENSIONS: continue
                if re.fullmatch(r"[A-Z][A-Z0-9]*(_[A-Z0-9]+)+", value): continue   # the NAME of a variable or secret, not its value
                if SYNTHETIC.search(value) or SYNTHETIC.search(line[: m.start()][-40:]):
                    continue
                if not re.search(r"[A-Za-z]", value) or not re.search(r"[0-9A-Z!@#$%^&*_\-]", value) and len(value) < 12:
                    continue
                if not allowed(entries, rel, "secret-assignment"):
                    findings.append((rel, n, "secret-assignment"))
    return findings


def main(argv):
    root = argv[1] if len(argv) > 1 else "."
    findings = scan(root)
    for rel, n, rule in findings:
        print(f"{rel}:{n}: possible secret ({rule}); the value is not shown")
    print(f"secrets scan: {'FAILED, ' + str(len(findings)) + ' finding(s)' if findings else 'no findings'} (pattern scan of tracked files; not a guarantee)")
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
