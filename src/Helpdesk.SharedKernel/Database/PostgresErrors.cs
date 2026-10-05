using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Helpdesk.SharedKernel.Database;

public static class PostgresErrors
{
    /// <summary>True when the exception is a PostgreSQL unique violation (23505); returns the violated constraint name.</summary>
    public static bool IsUniqueViolation(DbUpdateException exception, out string? constraintName)
    {
        if (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            constraintName = pg.ConstraintName;
            return true;
        }

        constraintName = null;
        return false;
    }
}
