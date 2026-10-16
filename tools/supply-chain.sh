#!/usr/bin/env bash
# Supply-chain checks (SEC-060 to SEC-062, task FND-07):
#   1. NuGet vulnerability gate (direct and transitive)   2. CycloneDX SBOM   3. licence scan against tools/licence-policy.json
# Outputs go to artifacts/ (git-ignored). Exit code is non-zero if a gate fails. Needs network access to NuGet for restore.
# The licence result is an engineering control, not legal approval.
set -uo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
mkdir -p artifacts/sbom artifacts/licences
status=0
dotnet tool restore >/dev/null || { echo "dotnet tool restore failed" >&2; exit 2; }

echo "== 1. Vulnerable packages (direct and transitive) =="
dotnet restore src/Ecp.sln >/dev/null || status=1
dotnet list src/Ecp.sln package --vulnerable --include-transitive 2>&1 | tee artifacts/vulnerabilities.txt
if grep -q "has the following vulnerable packages" artifacts/vulnerabilities.txt; then echo "FAIL: vulnerable packages found"; status=1; else echo "PASS: no known vulnerable packages"; fi

echo "== 2. SBOM (CycloneDX 1.6) =="
dotnet tool run dotnet-CycloneDX -- src/Ecp.sln -o artifacts/sbom -fn ecp.bom.json -F Json -spv 1.6 -sn "Enterprise Contract Platform" --exclude-test-projects >/dev/null \
  && python3 -I -c "import json;d=json.load(open('artifacts/sbom/ecp.bom.json'));print('SBOM', d['bomFormat'], d['specVersion'], len(d['components']), 'components')" \
  || { echo "FAIL: SBOM generation failed"; status=1; }

echo "== 3. Licence scan =="
rm -f artifacts/licences/licences.json
# nuget-license exits non-zero when it cannot resolve some licence expressions; the policy script below decides pass/fail.
dotnet tool run nuget-license -- -i src/Ecp.sln -t -o JsonPretty -fo artifacts/licences/licences.json >/dev/null || true
[[ -s artifacts/licences/licences.json ]] || { echo "FAIL: licence scan produced no output"; exit 1; }
python3 -I tools/check-licences.py artifacts/licences/licences.json tools/licence-policy.json artifacts/licences/licence-report.md || status=1
exit $status
