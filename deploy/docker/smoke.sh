#!/usr/bin/env bash
# Build the image, run it in Demo mode and probe the health endpoints. Prints a verification result.
# Usage: deploy/docker/smoke.sh [--network host]   (host networking is for sandboxes without a bridge network)
set -euo pipefail
cd "$(dirname "$0")/../.."
NET=(); [[ "${1:-}" == "--network" ]] && NET=(--network "$2")
PORT=${ECP_SMOKE_PORT:-18080}
docker build -f deploy/docker/Dockerfile -t ecp:smoke "${NET[@]}" .
docker rm -f ecp-smoke >/dev/null 2>&1 || true
docker run -d --name ecp-smoke "${NET[@]}" -e ASPNETCORE_URLS="http://127.0.0.1:$PORT" \
  -e Ecp__Mode=Demo -e Database__Provider=Sqlite -e Database__ConnectionString="Data Source=/var/ecp/ecp-demo.db" ecp:smoke >/dev/null
trap 'docker rm -f ecp-smoke >/dev/null 2>&1 || true' EXIT
for i in $(seq 1 40); do
  if curl -fsS "http://127.0.0.1:$PORT/health/ready" >/tmp/ecp-ready.json 2>/dev/null; then
    echo "VERIFIED: container started and /health/ready responded: $(cat /tmp/ecp-ready.json)"; exit 0
  fi; sleep 1
done
docker logs --tail 40 ecp-smoke >&2; echo "FAILED: container did not become ready" >&2; exit 1
