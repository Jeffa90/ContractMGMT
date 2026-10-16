#!/usr/bin/env bash
# Sandbox-friendly verification of the RUNTIME half of deploy/docker/Dockerfile.
# Use when the container build network cannot reach NuGet (e.g. an intercepting proxy whose CA the build stage does not trust).
# Publishes on the host, reuses the Dockerfile's runtime stage unchanged, runs the container, checks readiness,
# the non-root user and the Production refusal. It does NOT verify the SDK build stage; deploy/docker/smoke.sh does.
set -euo pipefail
cd "$(dirname "$0")/.."
W=$(mktemp -d); trap 'rm -rf "$W"; docker rm -f ecp-smoke >/dev/null 2>&1 || true' EXIT
dotnet publish src/Ecp.Host/Ecp.Host.csproj -c Release -o "$W/out" /p:UseAppHost=false >/dev/null
sed -e '/^FROM mcr.microsoft.com\/dotnet\/sdk/,/^RUN dotnet publish/d' -e 's#COPY --from=build /out .#COPY out/ .#' deploy/docker/Dockerfile > "$W/Dockerfile"
docker build --network host -q -t ecp:smoke "$W" >/dev/null
PORT=${ECP_SMOKE_PORT:-18080}
docker run -d --name ecp-smoke --network host -e ASPNETCORE_URLS="http://127.0.0.1:$PORT" -e Ecp__Mode=Demo \
  -e Database__Provider=Sqlite -e Database__ConnectionString="Data Source=/var/ecp/ecp-demo.db" ecp:smoke >/dev/null
for i in $(seq 1 40); do curl -fsS "http://127.0.0.1:$PORT/health/ready" >/dev/null 2>&1 && break; sleep 1; done
READY=$(curl -fsS "http://127.0.0.1:$PORT/health/ready") || { docker logs --tail 30 ecp-smoke >&2; echo "FAILED: not ready" >&2; exit 1; }
echo "$READY" | grep -q '"id":"R-OPS-02","status":"Healthy"' || { echo "FAILED: ICU/time-zone data missing in the image: $READY" >&2; exit 1; }
USER_ID=$(docker exec ecp-smoke id -u); [[ "$USER_ID" != "0" ]] || { echo "FAILED: runs as root" >&2; exit 1; }
if docker run --rm --network host -e Ecp__Mode=Production -e Database__Provider=Sqlite -e Database__ConnectionString="Data Source=x.db" ecp:smoke >/dev/null 2>&1; then
  echo "FAILED: Production accepted demo settings" >&2; exit 1; fi
echo "VERIFIED (runtime stage only): Demo ready, ICU and time-zone data present (R-OPS-02), non-root uid $USER_ID, Production refuses demo/SQLite settings."
