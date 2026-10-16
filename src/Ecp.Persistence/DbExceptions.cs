using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Persistence;

public static class DbExceptions
{
    /// <summary>True for a unique index or primary key violation on SQL Server (2601, 2627) or SQLite (constraint 19, UNIQUE).</summary>
    public static bool IsUniqueViolation(DbUpdateException ex) => ex.InnerException switch
    {
        SqlException s => s.Number is 2601 or 2627,
        SqliteException l => l.SqliteErrorCode == 19 && l.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase),
        _ => false
    };
}
