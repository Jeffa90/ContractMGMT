#!/usr/bin/env bash
# Sandbox-friendly verification of the RUNTIME half of deploy/docker/Dockerfile.
# Use when the container build network cannot reach NuGet (e.g. an intercepting proxy whose CA the build stage does not trust).
# Publishes on the host, reuses the Dockerfile's runtime stage unchanged, then runs the same behavioural checks as the full build
# (deploy/docker/verify-image.sh). It does NOT verify the SDK build stage; deploy/docker/smoke.sh does.
set -euo pipefail
cd "$(dirname "$0")/.."
W=$(mktemp -d); trap 'rm -rf "$W"' EXIT
dotnet publish src/Ecp.Host/Ecp.Host.csproj -c Release -o "$W/out" /p:UseAppHost=false >/dev/null
sed -e '/^FROM mcr.microsoft.com\/dotnet\/sdk/,/^RUN dotnet publish/d' -e '/^COPY templates\//d;/^COPY sample-data\//d;/^COPY requirements\//d;/^# Files outside src/d' -e 's#COPY --from=build /out .#COPY out/ .#' deploy/docker/Dockerfile > "$W/Dockerfile"
docker build --network host -q -t ecp:smoke "$W" >/dev/null
deploy/docker/verify-image.sh ecp:smoke --network host | sed 's/^VERIFIED:/VERIFIED (runtime stage only):/'
