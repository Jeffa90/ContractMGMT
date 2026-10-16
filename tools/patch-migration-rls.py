#!/usr/bin/env python3
"""Adds Row-Level Security policies (and the runtime grant) to a generated EF Core migration.

Usage: tools/patch-migration-rls.py <migration .cs file> <schema> <Table1,Table2,...>
Every NEW tenant-owned table must be listed (SECURITY.md, ADR-0003); the SQL Server test `Every_mapped_table_has_a_row_level_security_policy`
fails if one is missed. Idempotent: refuses to patch the same file twice."""
import re, sys

path, schema, tables = sys.argv[1], sys.argv[2], [t for t in sys.argv[3].split(',') if t]
src = open(path, encoding='utf-8').read()
if 'AddTenantPolicy' in src:
    sys.exit('already patched')
up_end = src.index('        /// <inheritdoc />\n        protected override void Down')
up_close = src.rindex('        }\n', 0, up_end)
up_lines = ''.join(f'            migrationBuilder.AddTenantPolicy("{schema}", "{t}");\n' for t in tables) + f'            migrationBuilder.GrantRuntimeAccess("{schema}");\n'
src = src[:up_close] + '\n' + up_lines + src[up_close:]
down_head = re.search(r'protected override void Down\(MigrationBuilder migrationBuilder\)\n        \{\n', src)
down_lines = ''.join(f'            migrationBuilder.DropTenantPolicy("{schema}", "{t}");\n' for t in tables) + '\n'
src = src[:down_head.end()] + down_lines + src[down_head.end():]
if 'using Ecp.Persistence;' not in src:
    src = src.replace('using Microsoft.EntityFrameworkCore.Migrations;', 'using Ecp.Persistence;\nusing Microsoft.EntityFrameworkCore.Migrations;', 1)
open(path, 'w', encoding='utf-8').write(src)
print('patched', path)
