#!/usr/bin/env python3
"""Regenerates the synthetic attachment placeholders and the sample ZIP in migration-samples/ (deterministic: same bytes every run).

The files are obviously synthetic: a tiny PDF and a tiny DOCX whose text says so. They exist so the sample contracts-import.csv and attachment-manifest.csv
can be loaded end to end through the Data migration screens. Never put real documents here.

Usage: python3 tools/generate-migration-sample-attachments.py [--check]
  --check   exit non-zero if the committed files differ from what the generator would write (used by tools/check-repo.py)
"""
import io, os, sys, zipfile

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "migration-samples")
FIXED = (2026, 1, 1, 0, 0, 0)

FILES = ["DEMO-AGR-0001/DEMO-AGR-0001_executed.pdf", "DEMO-AGR-0001/DEMO-AGR-0001_schedule.docx", "DEMO-AGR-0002/DEMO-AGR-0002_executed.pdf"]


def pdf(label: str) -> bytes:
    return f"%PDF-1.4\n% Synthetic placeholder: {label}\n1 0 obj\n<< /Type /Catalog >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n".encode("ascii")


def zipped(entries) -> bytes:
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        for name, data in entries:
            info = zipfile.ZipInfo(name, FIXED)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            z.writestr(info, data)
    return buf.getvalue()


def docx(label: str) -> bytes:
    return zipped([
        ("[Content_Types].xml", '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/xml"/></Types>'),
        ("word/document.xml", f'<?xml version="1.0" encoding="UTF-8"?><document><body><p>Synthetic placeholder: {label}</p></body></document>'),
    ])


def build():
    content = {
        FILES[0]: pdf("DEMO-AGR-0001 executed"),
        FILES[1]: docx("DEMO-AGR-0001 schedule"),
        FILES[2]: pdf("DEMO-AGR-0002 executed"),
    }
    with open(os.path.join(ROOT, "attachment-manifest.csv"), "rb") as f:
        manifest = f.read()
    outputs = {os.path.join("attachments", name): data for name, data in content.items()}
    outputs["sample-attachments.zip"] = zipped([("attachment-manifest.csv", manifest)] + [(n, content[n]) for n in FILES])
    return outputs


def main():
    check = "--check" in sys.argv
    bad = 0
    for rel, data in build().items():
        path = os.path.join(ROOT, rel)
        if check:
            if not os.path.exists(path) or open(path, "rb").read() != data:
                print(f"DIFFERS: migration-samples/{rel}"); bad += 1
        else:
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "wb") as f: f.write(data)
            print(f"wrote migration-samples/{rel} ({len(data)} bytes)")
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
