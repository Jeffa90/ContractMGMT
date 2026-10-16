#!/usr/bin/env bash
# Developer helper: regenerate the (uncommitted) Lifecycle Initial migration against SQL Server types.
set -euo pipefail
cd "$(dirname "$0")/.."
set -a; . .ecp-dev/test-sql.env; set +a
export Database__Provider=SqlServer Database__ConnectionString="$ECP_TEST_SQL;Database=ecp_migration_design" Database__AutoMigrate=false
export PATH="$PATH:$HOME/.dotnet/tools"
(cd src && dotnet build Ecp.Host/Ecp.Host.csproj -v q >/dev/null && dotnet ef migrations remove --context LifecycleDbContext --project Modules/Ecp.Lifecycle --startup-project Ecp.Host --force --no-build >/dev/null 2>&1 || true)
rm -rf src/Modules/Ecp.Lifecycle/Persistence/Migrations
tools/add-migration.sh Lifecycle "${1:-Initial}" "StatusHistory,LegalReview,LegalRound,LegalComment,LegalAttachment,LegalEvent,ExecutionRecord,ApprovalRequest,RecordUnlock,Variation,Renewal,LifecycleEvent,SubjectDocument" 2>&1 | grep -v "^warn\|SQLite\|^      " | tail -3
