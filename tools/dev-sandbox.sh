#!/usr/bin/env bash
# Prepare a fresh Linux (Ubuntu 24.04) cloud sandbox for development and testing. Idempotent. Needs root.
#  1. .NET 10 SDK from the distribution archive   2. dotnet-ef tool
#  3. container daemon (started without bridge/iptables if it is not running)   4. disposable SQL Server
# Anything that cannot be provided is reported; tests that depend on it are then NOT VERIFIED.
set -uo pipefail
cd "$(dirname "$0")/.."
ok()   { echo "[ok]    $*"; }
warn() { echo "[WARN]  $*"; }

if ! command -v dotnet >/dev/null; then
  apt-get update -q >/dev/null 2>&1; apt-get install -y -q dotnet-sdk-10.0 >/dev/null 2>&1 || warn "could not install dotnet-sdk-10.0"
fi
command -v dotnet >/dev/null && ok ".NET SDK $(dotnet --version)" || warn "no .NET SDK"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 PATH="$PATH:$HOME/.dotnet/tools"
command -v dotnet-ef >/dev/null || dotnet tool install --global dotnet-ef --version 10.0.12 >/dev/null 2>&1
command -v dotnet-ef >/dev/null && ok "dotnet-ef $(dotnet ef --version 2>/dev/null | tail -1)" || warn "dotnet-ef not installed"

if command -v docker >/dev/null && ! docker info >/dev/null 2>&1 && command -v dockerd >/dev/null; then
  (nohup dockerd --iptables=false --bridge=none >/tmp/dockerd.log 2>&1 &); sleep 12
fi
if docker info >/dev/null 2>&1; then
  ok "container engine reachable"
  tools/sqlserver-up.sh && ok "SQL Server ready; run:  set -a; . .ecp-dev/test-sql.env; set +a" || warn "SQL Server container failed"
else
  warn "no container engine; SQL Server tests will be NOT VERIFIED unless ECP_TEST_SQL is supplied"
fi
