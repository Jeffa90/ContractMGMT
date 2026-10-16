#!/usr/bin/env bash
# Add an EF Core migration for a module and apply project conventions.
# Usage: tools/add-migration.sh <Module> <MigrationName> [NewTenantTable1,NewTenantTable2]      e.g. tools/add-migration.sh Contracts AddRenewals Renewal
# The optional third argument lists NEW tenant-owned tables; Row-Level Security policies are added to the migration automatically.
# Modules: Audit Tenancy Contracts Documents DataMigration Identity ...  (the folder under src/Modules/Ecp.<Module>)
# After generating, add Row-Level Security / grants for any NEW tenant-owned table (see RowLevelSecurity helpers) and review the SQL.
set -euo pipefail
cd "$(dirname "$0")/../src"
export PATH="$PATH:$HOME/.dotnet/tools" DOTNET_CLI_TELEMETRY_OPTOUT=1
MODULE=${1:?module}; NAME=${2:?name}
CTX="${MODULE}DbContext"
[[ "$MODULE" == "DataMigration" ]] && CTX="MigrationDbContext"   # context kept its original name (ADR-0015)
dotnet build Ecp.Host/Ecp.Host.csproj -v q >/dev/null
dotnet ef migrations add "$NAME" --context "$CTX" --project "Modules/Ecp.$MODULE" --startup-project Ecp.Host --output-dir Persistence/Migrations --no-build
if [[ -n "${3:-}" ]]; then
  SCHEMA=$(grep -o 'Schema = "[a-z]*"' "Modules/Ecp.$MODULE"/*.cs | head -1 | sed 's/.*"\(.*\)"/\1/')
  FILE=$(ls "Modules/Ecp.$MODULE/Persistence/Migrations/"*_"$NAME".cs | head -1)
  python3 ../tools/patch-migration-rls.py "$FILE" "$SCHEMA" "$3"
fi
echo "Review the generated migration, then run the tests."
