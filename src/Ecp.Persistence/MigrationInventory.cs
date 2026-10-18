using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Ecp.Persistence;

/// <summary>What a migration does to existing structure or data, in the words an upgrade reviewer uses (docs/UPGRADES.md, "Migration safety").</summary>
public static class MigrationCategories
{
    public const string Destructive = "Destructive";               // drops a table, column or schema: data can be lost
    public const string DataRewrite = "DataRewrite";               // inserts, updates or deletes rows
    public const string SchemaAlteration = "SchemaAlteration";     // changes or renames something that already exists
    public const string IndexOrConstraint = "IndexOrConstraint";   // indexes, keys, foreign keys and checks
    public const string RowLevelSecurity = "RowLevelSecurity";     // tenant policies, predicate functions and grants
    public const string RawSql = "RawSql";                         // hand-written SQL that fits no other category

    /// <summary>Categories that need a recorded review before they may ship (the review register, <c>upgrades/migration-reviews.json</c>).</summary>
    public static readonly IReadOnlySet<string> NeedReview = new HashSet<string>(StringComparer.Ordinal) { Destructive, DataRewrite, SchemaAlteration };
    public static readonly IReadOnlyList<string> All = [Destructive, DataRewrite, SchemaAlteration, IndexOrConstraint, RowLevelSecurity, RawSql];
}

/// <summary>One migration as compiled into the platform: its identifier, a checksum of the SQL it generates, what kinds of change it makes and which objects it touches. Checksums exclude the history row, so they do not depend on the provider's product version.</summary>
public sealed record MigrationEntry(string Id, string Checksum, IReadOnlyList<string> Categories, IReadOnlyList<string> Objects, int Operations);

public sealed record ModuleMigrations(string Module, int Tier, string Schema, string SchemaVersion, IReadOnlyList<MigrationEntry> Migrations);

/// <summary>Every migration of every module in deterministic order (tier, then module name; migrations by identifier).</summary>
public sealed record MigrationLedger(string Generator, IReadOnlyList<ModuleMigrations> Modules)
{
    public IEnumerable<(string Module, MigrationEntry Entry)> All => Modules.SelectMany(m => m.Migrations.Select(e => (m.Module, e)));
    public ModuleMigrations? Find(string module) => Modules.FirstOrDefault(m => string.Equals(m.Module, module, StringComparison.Ordinal));
}

/// <summary>Builds the ledger from the compiled migrations without touching any database.</summary>
public static class MigrationInventory
{
    private static readonly Regex DataVerbs = new(@"(?:^|;|\bBEGIN\b|\bTHEN\b|\bELSE\b|\)\s)\s*(?:UPDATE\s+\S+\s+SET\b|DELETE\s+FROM\b|INSERT\s+INTO\b|MERGE\s+(?:INTO\s+)?\S+|TRUNCATE\s+TABLE\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex Definition = new(@"\bCREATE\s+(?:OR\s+ALTER\s+)?(?:TRIGGER|FUNCTION|PROCEDURE|VIEW)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex DropVerbs = new(@"\bDROP\s+(TABLE|COLUMN|SCHEMA|DATABASE)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex SecurityVerbs = new(@"SECURITY\s+POLICY|FILTER\s+PREDICATE|BLOCK\s+PREDICATE|security\.fn_|\bGRANT\b|CREATE\s+ROLE|CREATE\s+SCHEMA\s+.*security", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex IndexVerbs = new(@"\b(INDEX|CONSTRAINT)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    public static string GeneratorVersion => typeof(DbContext).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    public static MigrationLedger Build(IEnumerable<ModuleDescriptor> modules)
    {
        var result = new List<ModuleMigrations>();
        foreach (var module in modules.OrderBy(m => m.Tier).ThenBy(m => m.Name, StringComparer.Ordinal))
        {
            using var context = DesignContext(module);
            var assembly = context.GetService<IMigrationsAssembly>();
            var generator = context.GetService<IMigrationsSqlGenerator>();
            var initializer = context.GetService<IModelRuntimeInitializer>();
            var entries = new List<MigrationEntry>();
            foreach (var (id, type) in assembly.Migrations.OrderBy(m => m.Key, StringComparer.Ordinal))
            {
                var migration = assembly.CreateMigration(type, context.Database.ProviderName!);
                var model = migration.TargetModel as IModel;
                if (model is IMutableModel mutable) model = mutable.FinalizeModel();
                if (model is not null) model = initializer.Initialize(model, designTime: true, validationLogger: null);
                var commands = generator.Generate(migration.UpOperations, model, MigrationsSqlGenerationOptions.NoTransactions);
                var text = string.Join("\n--\n", commands.Select(c => Normalise(c.CommandText)));
                var (categories, objects) = Classify(migration.UpOperations);
                entries.Add(new MigrationEntry(id, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))), categories, objects, migration.UpOperations.Count));
            }
            result.Add(new ModuleMigrations(module.Name, module.Tier, module.Schema, entries.LastOrDefault()?.Id ?? "", entries));
        }
        return new MigrationLedger(GeneratorVersion, result);
    }

    /// <summary>Opens a design-time context for the module: SQL Server provider services and the module's migrations, no connection.</summary>
    private static DbContext DesignContext(ModuleDescriptor module)
    {
        var create = typeof(DesignTime).GetMethod(nameof(DesignTime.Create))!.MakeGenericMethod(module.DbContextType).Invoke(null, [module.Schema])!;
        var options = create.GetType().GetField("Item1")!.GetValue(create)!;
        var services = create.GetType().GetField("Item2")!.GetValue(create)!;
        return (DbContext)Activator.CreateInstance(module.DbContextType, options, services)!;
    }

    private static string Normalise(string sql) => sql.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    public static (IReadOnlyList<string> Categories, IReadOnlyList<string> Objects) Classify(IReadOnlyList<MigrationOperation> operations)
    {
        var categories = new SortedSet<string>(StringComparer.Ordinal);
        var objects = new List<string>();
        void Touch(string kind, string? schema, string? name, string? column = null) => objects.Add($"{kind} {(schema is null ? "" : schema + ".")}{name}{(column is null ? "" : "." + column)}".Trim());
        foreach (var op in operations)
        {
            switch (op)
            {
                case CreateTableOperation o: Touch("create table", o.Schema, o.Name); break;
                case AddColumnOperation o: Touch("add column", o.Schema, o.Table, o.Name); break;
                case EnsureSchemaOperation o: Touch("ensure schema", null, o.Name); break;
                case DropTableOperation o: categories.Add(MigrationCategories.Destructive); Touch("DROP TABLE", o.Schema, o.Name); break;
                case DropColumnOperation o: categories.Add(MigrationCategories.Destructive); Touch("DROP COLUMN", o.Schema, o.Table, o.Name); break;
                case DropSchemaOperation o: categories.Add(MigrationCategories.Destructive); Touch("DROP SCHEMA", null, o.Name); break;
                case AlterColumnOperation o: categories.Add(MigrationCategories.SchemaAlteration); Touch("ALTER COLUMN", o.Schema, o.Table, o.Name); break;
                case AlterTableOperation o: categories.Add(MigrationCategories.SchemaAlteration); Touch("ALTER TABLE", o.Schema, o.Name); break;
                case RenameTableOperation o: categories.Add(MigrationCategories.SchemaAlteration); Touch("RENAME TABLE", o.Schema, o.Name); break;
                case RenameColumnOperation o: categories.Add(MigrationCategories.SchemaAlteration); Touch("RENAME COLUMN", o.Schema, o.Table, o.Name); break;
                case RenameIndexOperation o: categories.Add(MigrationCategories.IndexOrConstraint); Touch("rename index", o.Schema, o.Table, o.Name); break;
                case CreateIndexOperation o: categories.Add(MigrationCategories.IndexOrConstraint); Touch("create index", o.Schema, o.Table, o.Name); break;
                case DropIndexOperation o: categories.Add(MigrationCategories.IndexOrConstraint); Touch("DROP INDEX", o.Schema, o.Table, o.Name); break;
                case AddForeignKeyOperation o: categories.Add(MigrationCategories.IndexOrConstraint); Touch("add foreign key", o.Schema, o.Table, o.Name); break;
                case DropForeignKeyOperation o: categories.Add(MigrationCategories.IndexOrConstraint); Touch("DROP FOREIGN KEY", o.Schema, o.Table, o.Name); break;
                case AddPrimaryKeyOperation o: categories.Add(MigrationCategories.IndexOrConstraint); Touch("add primary key", o.Schema, o.Table, o.Name); break;
                case DropPrimaryKeyOperation o: categories.Add(MigrationCategories.IndexOrConstraint); Touch("DROP PRIMARY KEY", o.Schema, o.Table, o.Name); break;
                case AddUniqueConstraintOperation o: categories.Add(MigrationCategories.IndexOrConstraint); Touch("add unique constraint", o.Schema, o.Table, o.Name); break;
                case DropUniqueConstraintOperation o: categories.Add(MigrationCategories.IndexOrConstraint); Touch("DROP UNIQUE CONSTRAINT", o.Schema, o.Table, o.Name); break;
                case AddCheckConstraintOperation o: categories.Add(MigrationCategories.IndexOrConstraint); Touch("add check constraint", o.Schema, o.Table, o.Name); break;
                case DropCheckConstraintOperation o: categories.Add(MigrationCategories.IndexOrConstraint); Touch("DROP CHECK CONSTRAINT", o.Schema, o.Table, o.Name); break;
                case InsertDataOperation o: categories.Add(MigrationCategories.DataRewrite); Touch("insert data", o.Schema, o.Table); break;
                case UpdateDataOperation o: categories.Add(MigrationCategories.DataRewrite); Touch("UPDATE data", o.Schema, o.Table); break;
                case DeleteDataOperation o: categories.Add(MigrationCategories.DataRewrite); Touch("DELETE data", o.Schema, o.Table); break;
                case SqlOperation o: ClassifySql(o.Sql, categories, objects); break;
                default: categories.Add(MigrationCategories.SchemaAlteration); objects.Add(op.GetType().Name); break;
            }
        }
        return (categories.ToList(), objects.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());
    }

    private static void ClassifySql(string sql, SortedSet<string> categories, List<string> objects)
    {
        var flat = Regex.Replace(sql, @"\s+", " ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)).Trim();
        var matched = false;
        if (DropVerbs.IsMatch(flat)) { categories.Add(MigrationCategories.Destructive); matched = true; }
        if (SecurityVerbs.IsMatch(flat)) { categories.Add(MigrationCategories.RowLevelSecurity); matched = true; }
        else if (!Definition.IsMatch(flat) && DataVerbs.IsMatch(flat)) { categories.Add(MigrationCategories.DataRewrite); matched = true; }
        else if (Definition.IsMatch(flat)) { categories.Add(MigrationCategories.RawSql); matched = true; }
        else if (IndexVerbs.IsMatch(flat)) { categories.Add(MigrationCategories.IndexOrConstraint); matched = true; }
        if (!matched) categories.Add(MigrationCategories.RawSql);
        objects.Add("sql: " + (flat.Length <= 90 ? flat : flat[..90] + "…"));
    }
}
