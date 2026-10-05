using System.ComponentModel.DataAnnotations;

namespace Helpdesk.SharedKernel.Database;

public sealed class DatabaseOptions
{
    public const string Section = "Database";

    [Required]
    public string ConnectionString { get; init; } = "";

    /// <summary>Apply pending migrations when the web host starts. Default false; production uses the explicit <c>migrate</c> command.</summary>
    public bool MigrateOnStartup { get; init; }
}
