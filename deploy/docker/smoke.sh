#!/usr/bin/env bash
# Build the image from deploy/docker/Dockerfile (the full SDK build) and run the behavioural checks in verify-image.sh.
# Usage: deploy/docker/smoke.sh [--network host]   (host networking is for sandboxes without a bridge network)
set -euo pipefail
cd "$(dirname "$0")/../.."
NET=(); [[ "${1:-}" == "--network" ]] && NET=(--network "$2")
docker build -f deploy/docker/Dockerfile -t ecp:smoke "${NET[@]}" .
deploy/docker/verify-image.sh ecp:smoke "${NET[@]}"
