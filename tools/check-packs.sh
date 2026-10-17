#!/usr/bin/env bash
# Solution-pack checks (CI and local): build, validate, prove the build is reproducible, sign and verify with an EPHEMERAL key that exists only for this run,
# show that tampering and a wrong key are refused as Production would refuse them, and confirm that no pack is an embedded resource and no private key is committed.
# Usage: tools/check-packs.sh   (after `dotnet build src/Ecp.sln -c Release`)
set -euo pipefail
cd "$(dirname "$0")/.."
TOOL=(dotnet src/Ecp.PackTool/bin/Release/net10.0/Ecp.PackTool.dll)
[[ -f src/Ecp.PackTool/bin/Release/net10.0/Ecp.PackTool.dll ]] || { echo "Build the solution first: dotnet build src/Ecp.sln -c Release" >&2; exit 2; }
WORK=$(mktemp -d); trap 'rm -rf "$WORK"' EXIT
fail() { echo "FAILED: $*" >&2; exit 1; }

echo "== build twice and compare (reproducibility of the unsigned packages)"
"${TOOL[@]}" build-all --source templates/packs --out "$WORK/a" >/dev/null
"${TOOL[@]}" build-all --source templates/packs --out "$WORK/b" >/dev/null
COUNT=0
for f in "$WORK"/a/*.ecpkg; do
  "${TOOL[@]}" compare --a "$f" --b "$WORK/b/$(basename "$f")" >/dev/null || fail "$(basename "$f") is not reproducible"
  cmp -s "$f" "$WORK/b/$(basename "$f")" || fail "$(basename "$f") differs byte for byte between two builds"
  "${TOOL[@]}" validate --package "$f" >/dev/null || fail "$(basename "$f") does not validate"
  COUNT=$((COUNT + 1))
done
[[ $COUNT -ge 2 ]] || fail "expected at least the Blank and the reference pack ($COUNT built)"
echo "   $COUNT packs built, identical on both builds, valid"

echo "== the host output carries the shipped packs as external files"
for f in "$WORK"/a/*.ecpkg; do
  [[ -f "src/Ecp.Host/bin/Release/net10.0/packs/$(basename "$f")" ]] || fail "the host build did not produce packs/$(basename "$f")"
  cmp -s "$f" "src/Ecp.Host/bin/Release/net10.0/packs/$(basename "$f")" || fail "packs/$(basename "$f") in the host output differs from a fresh build"
done

echo "== sign and verify with an ephemeral key (never written to the repository)"
"${TOOL[@]}" keygen --out-private "$WORK/ci.key" --out-public "$WORK/ci.pub.pem" >/dev/null
"${TOOL[@]}" keygen --out-private "$WORK/other.key" --out-public "$WORK/other.pub.pem" >/dev/null
PACK=$(ls "$WORK"/a/*.ecpkg | head -n 1)
"${TOOL[@]}" sign --package "$PACK" --out "$WORK/signed.ecpkg" --key-id ci-ephemeral --key-file "$WORK/ci.key" >/dev/null
"${TOOL[@]}" verify --package "$WORK/signed.ecpkg" --public-key "$WORK/ci.pub.pem" --key-id ci-ephemeral --production >/dev/null || fail "a correctly signed pack was refused"
"${TOOL[@]}" verify --package "$PACK" --public-key "$WORK/ci.pub.pem" --key-id ci-ephemeral --production >/dev/null 2>&1 && fail "an unsigned pack was accepted as Production"
"${TOOL[@]}" verify --package "$WORK/signed.ecpkg" --public-key "$WORK/other.pub.pem" --key-id ci-ephemeral --production >/dev/null 2>&1 && fail "a pack signed by a different key was accepted"
python3 -I - "$WORK/signed.ecpkg" "$WORK/tampered.ecpkg" <<'PY'
import sys, zipfile
src, dst = sys.argv[1:3]
with zipfile.ZipFile(src) as z, zipfile.ZipFile(dst, "w", zipfile.ZIP_STORED) as out:
    for i in z.infolist():
        data = z.read(i.filename)
        if i.filename.startswith("content/") and i.filename.endswith(".json"): data = data + b" "
        out.writestr(i.filename, data)
PY
"${TOOL[@]}" verify --package "$WORK/tampered.ecpkg" --public-key "$WORK/ci.pub.pem" --key-id ci-ephemeral --production >/dev/null 2>&1 && fail "a tampered pack was accepted"
echo "   signed pack accepted; unsigned, wrong-key and tampered packs refused"

echo "== no pack is an embedded resource; no private key is tracked"
if grep -rEn 'EmbeddedResource' --include=*.csproj src tests | grep -Ei 'templates[/\\]packs|pack\.json|contract-lifecycle|variation\.json|notification-policy'; then fail "a pack document is embedded in an assembly"; fi
if git ls-files | grep -E '\.(pem|key|pfx|p12)$' | grep -v '^tests/'; then fail "a key file is tracked"; fi
if git grep -nE -e '-----BEGIN [A-Z ]*PRIVATE KEY-----[[:space:]]*[A-Za-z0-9+/=]{40,}' -- . ':!tests' ; then fail "a private key block is committed"; fi
echo "OK: solution-pack checks passed"
