#!/usr/bin/env bash
# Add an EF Core migration for a module and apply project conventions.
# Usage: tools/add-migration.sh <Module> <MigrationName>      e.g. tools/add-migration.sh Contracts AddRenewals
# Modules: Audit Tenancy Contracts Documents Migration
# After generating, add Row-Level Security / grants for any NEW tenant-owned table (see RowLevelSecurity helpers) and review the SQL.
set -euo pipefail
cd "$(dirname "$0")/../src"
export PATH="$PATH:$HOME/.dotnet/tools" DOTNET_CLI_TELEMETRY_OPTOUT=1
MODULE=${1:?module}; NAME=${2:?name}
CTX="${MODULE}DbContext"
dotnet build Ecp.Host/Ecp.Host.csproj -v q >/dev/null
dotnet ef migrations add "$NAME" --context "$CTX" --project "Modules/Ecp.$MODULE" --startup-project Ecp.Host --output-dir Persistence/Migrations --no-build
if [[ "$MODULE" == "Migration" ]]; then
  # Namespace Ecp.Migration shadows the EF base class name.
  sed -i 's/ : Migration$/ : Microsoft.EntityFrameworkCore.Migrations.Migration/' Modules/Ecp.Migration/Persistence/Migrations/*_"$NAME"*.cs
fi
echo "Review the generated migration, then run the tests."
