#!/usr/bin/env python3
"""Drives the Data migration screens of a RUNNING Demo host over HTTP, exactly as a person would in a browser (sign in, antiforgery tokens, file uploads),
loading the repository's synthetic samples: contracts-import.csv + attachment-manifest.csv (inside sample-attachments.zip) + the placeholder attachments.

Usage:
  ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://127.0.0.1:5088 dotnet src/Ecp.Host/bin/Debug/net10.0/Ecp.Host.dll &   # seeded synthetic Demo
  python3 -I tools/migration-demo.py [--base http://127.0.0.1:5088]

Prints each step and what the application then shows. Exits non-zero on the first thing that does not hold. Synthetic data only; never point this at a real system.
Standard library only.
"""
import argparse, datetime, http.client, os, re, sys, urllib.parse, uuid

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
SAMPLES = os.path.join(ROOT, "migration-samples")
GUID = r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"


class Person:
    """A browser session: cookies, redirects not followed, antiforgery token handling."""

    def __init__(self, base):
        u = urllib.parse.urlparse(base)
        self.host, self.port, self.cookies = u.hostname, u.port or 80, {}

    def request(self, method, path, body=None, headers=None):
        c = http.client.HTTPConnection(self.host, self.port, timeout=120)
        h = {"Cookie": "; ".join(f"{k}={v}" for k, v in self.cookies.items())} if self.cookies else {}
        h.update(headers or {})
        c.request(method, path, body=body, headers=h)
        r = c.getresponse()
        data = r.read()
        for header, value in r.getheaders():
            if header.lower() == "set-cookie":
                name, _, rest = value.partition("=")
                val = rest.split(";", 1)[0]
                if val == "" or "expires=Thu, 01 Jan 1970" in value: self.cookies.pop(name, None)
                else: self.cookies[name] = val
        return r.status, dict((k.lower(), v) for k, v in r.getheaders()), data

    def get(self, path):
        status, headers, data = self.request("GET", path)
        if status >= 400: raise SystemExit(f"GET {path} -> {status}: {data[:300]!r}")
        return data.decode("utf-8", "replace")

    def token(self, page):
        m = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', self.get(page))
        if not m: raise SystemExit(f"no antiforgery token on {page}")
        return m.group(1)

    def post(self, page, handler=None, fields=(), files=()):
        fields = list(fields) + [("__RequestVerificationToken", self.token(page))]
        url = page + (f"?handler={handler}" if handler else "")
        if files:
            boundary = "----ecp" + uuid.uuid4().hex
            parts = []
            for k, v in fields: parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{k}"\r\n\r\n{v}\r\n'.encode())
            for k, name, content in files:
                parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{k}"; filename="{name}"\r\nContent-Type: application/octet-stream\r\n\r\n'.encode() + content + b"\r\n")
            body = b"".join(parts) + f"--{boundary}--\r\n".encode()
            status, headers, data = self.request("POST", url, body, {"Content-Type": f"multipart/form-data; boundary={boundary}"})
        else:
            status, headers, data = self.request("POST", url, urllib.parse.urlencode(fields).encode(), {"Content-Type": "application/x-www-form-urlencoded"})
        if status not in (200, 302): raise SystemExit(f"POST {url} -> {status}: {data[:500]!r}")
        return status, headers.get("location"), data.decode("utf-8", "replace")


def step(text): print(f"\n== {text}")
def ok(text): print(f"   ok  {text}")


def need(cond, text):
    if not cond: raise SystemExit(f"   FAILED: {text}")
    ok(text)


def sign_in(base, email):
    p = Person(base)
    html = p.get("/SignIn")
    row = re.search(rf'name="pick" value="({GUID}:{GUID})"[^>]*>[^<]*</button></td>\s*<td>[^<]*<br /><span class="hint">{re.escape(email)}</span>', html)
    need(row is not None, f"{email} is offered on the Demo sign-in page")
    status, _, _ = p.post("/SignIn", None, [("pick", row.group(1)), ("returnUrl", "/")])
    need(status in (200, 302), f"signed in as {email}")
    return p


def sample(name): return open(os.path.join(SAMPLES, name), "rb").read()


def main():
    ap = argparse.ArgumentParser(); ap.add_argument("--base", default="http://127.0.0.1:5088"); base = ap.parse_args().base
    admin = sign_in(base, "procadmin@a.example.test")
    second = sign_in(base, "procadmin2@a.example.test")

    step("1. Migration project")
    home = admin.get("/Migration")
    org = re.search(rf'<option value="({GUID})"', home).group(1)
    today = datetime.date.today()
    run = datetime.datetime.now().strftime("%H%M%S")   # names must be unique per organisation, so the script can be repeated against the same host
    _, project_url, _ = admin.post("/Migration", None, [("OrganisationId", org), ("Name", f"Sample register load {run}"), ("SourceSystem", "Medius"), ("SourceSystemVersion", "2026.1"),
                                                         ("DataOwner", "Data Owner"), ("ExtractionDate", str(today - datetime.timedelta(days=2)))])
    need(project_url and "/Migration/Projects/" in project_url, f"project created at {project_url}")

    step("2. Mapping profile from the samples (field mappings CSV + status lookup CSV), published")
    _, profile_url, _ = admin.post("/Migration/Profiles", None, [("OrganisationId", org), ("Name", f"Sample register profile {run}"), ("SourceSystem", "Medius"), ("SourceSystemVersion", "2026.1")])
    version = re.search(rf'name="VersionId" value="({GUID})"', admin.get(profile_url)).group(1)
    admin.post(profile_url, "Fields", [("VersionId", version)], [("file", "field-mapping-profile-complete.csv", sample("field-mapping-profile-complete.csv"))])
    admin.post(profile_url, "Lookups", [("VersionId", version)], [("file", "status-lookup-map.csv", sample("status-lookup-map.csv"))])
    admin.post(profile_url, "Options", [("VersionId", version), ("OpenEndedTypes", "Evergreen")])
    admin.post(profile_url, "Publish", [("VersionId", version), ("PublishNote", "Sample profile")])
    need("published, unchangeable" in admin.get(f"{profile_url}?versionId={version}"), "profile version published and unchangeable")

    step("3. Legacy-load policy and a legacy final batch (profile pinned)")
    admin.post(project_url, "Legacy", [("AllowedStatuses", "Effective, Ended"), ("AllowRegisteredDate", "true"), ("ExpiresDate", str(today + datetime.timedelta(days=30))), ("AllowOverwrite", "false")])
    _, batch_url, _ = admin.post(project_url, "CreateBatch", [("BatchName", f"Sample final load {run}"), ("Kind", "Final"), ("Strategy", "SkipExisting"), ("DocumentOption", "AddVersion"),
                                                               ("IsLegacyLoad", "true"), ("ProfileVersionId", version)])
    batch_id = re.search(GUID, batch_url).group(0)
    need(True, f"batch {batch_id} created")

    step("4. Upload contracts-import.csv and the attachment ZIP (attachment-manifest.csv is read from its root)")
    admin.post(batch_url, "UploadContracts", [], [("file", "contracts-import.csv", sample("contracts-import.csv"))])
    admin.post(batch_url, "UploadArchive", [], [("file", "attachments.zip", sample("sample-attachments.zip"))])
    page = admin.get(batch_url)
    need("contracts-import.csv" in page and "attachments.zip" in page and "attachment-manifest.csv" in page, "contracts file, archive and the manifest inside it are registered")

    step("5. Dry run (nothing is written to the target)")
    admin.post(batch_url, "Validate")
    page = admin.get(batch_url)
    need("would load" in page and "Validated" in page, "dry run completed; the page shows what would load")
    need("DEMO-AGR-0001" not in admin.get("/Contracts?q=DEMO-AGR"), "the register is still empty of the sample contracts")

    step("6. Legacy load: a second person confirms exactly what was validated; the committer cannot confirm for themselves")
    status, _, body = admin.post(batch_url, "Commit")
    need("second person" in (admin.get(batch_url) if status == 302 else body), "commit refused before a second person confirms")
    second.post(batch_url, "Confirm")
    admin.post(batch_url, "Commit")
    need("Committed" in admin.get(batch_url), "batch committed")

    step("7. Reconciliation and acceptance")
    admin.post(batch_url, "Reconcile")
    need("Balanced" in admin.get(f"{batch_url}/Reconciliation"), "reconciliation balanced")
    admin.post(batch_url, "Accept", [("Note", "Checked both contracts and three documents against the source."), ("Acknowledge", "true")])
    need("Accepted" in admin.get(batch_url), "batch accepted")

    step("8. What the application now shows")
    register = admin.get("/Contracts?q=DEMO-AGR")
    need("DEMO-AGR-0001" in register and "DEMO-AGR-0002" in register, "Contracts: DEMO-AGR-0001 and DEMO-AGR-0002 are listed")
    cid = re.search(rf'/Contracts/Details/({GUID})', register).group(1)
    details = admin.get(f"/Contracts/Details/{cid}")
    need("Migration provenance" in details and ("MED-1001" in details or "MED-1002" in details), "contract details show Migration provenance (source record id)")
    docs = re.findall(rf'href="(/Documents/Download/{GUID})"', admin.get("/Contracts?q=DEMO-AGR-0001") + "".join(
        admin.get(f"/Contracts/Details/{m}") for m in re.findall(rf'/Contracts/Details/({GUID})', register)))
    need(len(set(docs)) >= 3, f"Documents: {len(set(docs))} downloadable documents are attached to the migrated contracts")
    status, headers, data = admin.request("GET", sorted(set(docs))[0])
    need(status == 200, "a migrated document downloads (HTTP 200)")
    need("Southern Cross Demo Supplies" in admin.get("/Suppliers"), "Suppliers: Southern Cross Demo Supplies Pty Ltd was created")
    prov = admin.get(f"{batch_url}/Provenance")
    need("MED-1001" in prov and "Document" in prov, "Provenance ledger lists contracts, suppliers and documents created by the batch")
    auditor = sign_in(base, "auditor@a.example.test")
    audit = auditor.get(f"/Admin/Audit?batch={batch_id}")
    need("migration.contract.legacy_loaded" in audit and "migration.document.imported" in audit, "Audit entries carry the batch id (legacy_loaded, document.imported)")
    errors = admin.request("GET", f"{batch_url}?handler=ErrorsXlsx")
    need(errors[0] == 200 and errors[2][:2] == b"PK", "the error report downloads as a real workbook")
    print("\nEND-TO-END MIGRATION VIA THE UI: ALL STEPS PASSED")


if __name__ == "__main__":
    main()
