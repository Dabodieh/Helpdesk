using System.ComponentModel.DataAnnotations;

namespace Helpdesk.Host.Configuration;

public sealed class DatabaseOptions
{
    public const string Section = "Database";

    [Required]
    public string ConnectionString { get; init; } = "";
}
