# ADR-0015: Bulk migration module is named `Ecp.DataMigration`

Status: Accepted (2026-10-17)

## Decision
The bulk data-migration module (migration projects, batches, staging, reconciliation) uses the assembly and namespace **`Ecp.DataMigration`** (folder `src/Modules/Ecp.DataMigration`). The business term, the module descriptor name (`Migration`), the database schema (`migration`), the DbContext class (`MigrationDbContext`) and the requirements vocabulary are unchanged.

## Context
The namespace `Ecp.Migration` made the simple name `Migration` ambiguous with `Microsoft.EntityFrameworkCore.Migrations.Migration` inside that module, so every generated EF Core migration had to be patched to a fully qualified base class (a script workaround recorded at M1). Further migration implementation (M6 onwards) would multiply the workaround.

## Alternatives considered
- Keep `Ecp.Migration` and the post-generation patch: fragile, and a forgotten patch fails the build only in that module.
- Rename the module to `Ecp.Onboarding` or `Ecp.Import`: collides with other planned concepts (organisation onboarding, bulk import features).

## Consequences
- Existing generated migration files keep their fully qualified base class; they continue to compile and apply (migration ids and the history table are unchanged, so existing databases are unaffected). New migrations generated for this module no longer need patching; `tools/add-migration.sh` accepts the module folder name (`DataMigration`) and maps the context.
- Verified after the rename: Release build, 17 lightweight-provider tests, and the SQL Server migration tests (`Migrations_create_every_module_schema_and_are_idempotent`) against SQL Server 2022.
- Documentation and the package manifest refer to `Ecp.DataMigration`.
