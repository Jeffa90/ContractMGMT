#!/usr/bin/env bash
# Verifies the Docker Compose files where Docker and Compose are available (FND-06).
#   1. Static validation of compose.yml and compose.production.yml (`docker compose config`).
#   2. Runs compose.yml (Demo) with the RUNTIME stage of the Dockerfile (the same shortcut tools/docker-runtime-smoke.sh uses when the build network cannot
#      reach NuGet): healthy by the compose healthcheck, read-only root filesystem, writable data volume, non-root, /health/ready answers.
# It does NOT verify the SDK build stage (deploy/docker/smoke.sh and the CI docker-image job do) and does not run compose.production.yml against a
# managed SQL Server. Nothing here disables TLS or certificate validation.
set -euo pipefail
cd "$(dirname "$0")/.."
command -v docker >/dev/null && docker compose version >/dev/null 2>&1 || { echo "NOT VERIFIED: Docker Compose is not available here." >&2; exit 3; }
W=$(mktemp -d); trap 'rm -rf "$W"; docker compose -f deploy/docker/compose.yml down -v >/dev/null 2>&1 || true; rm -rf deploy/docker/secrets' EXIT

mkdir -p deploy/docker/secrets && echo "Server=placeholder;Database=placeholder" > deploy/docker/secrets/ecp-sql.txt   # git-ignored; the file only has to exist for config validation
docker compose -f deploy/docker/compose.yml config -q && echo "compose.yml: configuration valid"
docker compose -f deploy/docker/compose.production.yml config -q && echo "compose.production.yml: configuration valid (not run)"

dotnet publish src/Ecp.Host/Ecp.Host.csproj -c Release -o "$W/out" /p:UseAppHost=false >/dev/null
sed -e '/^FROM mcr.microsoft.com\/dotnet\/sdk/,/^RUN dotnet publish/d' -e '/^COPY templates\//d;/^COPY sample-data\//d;/^COPY requirements\//d;/^# Files outside src/d' -e 's#COPY --from=build /out .#COPY out/ .#' deploy/docker/Dockerfile > "$W/Dockerfile"
docker build --network host -q -t ecp:local "$W" >/dev/null
docker compose -f deploy/docker/compose.yml up -d --no-build >/dev/null
for _ in $(seq 1 20); do
  s=$(docker compose -f deploy/docker/compose.yml ps --format '{{.Status}}' | head -1)
  [[ "$s" == *"(healthy)"* ]] && break; sleep 6
done
[[ "$s" == *"(healthy)"* ]] || { echo "FAILED: the container did not become healthy ($s)" >&2; docker compose -f deploy/docker/compose.yml logs --tail 30 >&2; exit 1; }
C=$(docker compose -f deploy/docker/compose.yml ps -q ecp)
docker exec "$C" sh -c 'touch /app/x' 2>/dev/null && { echo "FAILED: the root filesystem is writable" >&2; exit 1; } || echo "root filesystem is read-only"
docker exec "$C" sh -c 'touch /var/ecp/.w && rm /var/ecp/.w' && echo "data volume is writable"
[[ "$(docker exec "$C" id -u)" != "0" ]] && echo "runs as a non-root user"
docker exec "$C" bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET /health/ready HTTP/1.0\r\n\r\n" >&3 && grep -q "\"status\":\"Healthy\"" <&3' && echo "/health/ready answers Healthy"
echo "VERIFIED (compose.yml, runtime stage): healthy under the compose settings. compose.production.yml: configuration valid only (Scaffolded / Not Verified at run time)."
