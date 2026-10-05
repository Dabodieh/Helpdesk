using Helpdesk.Modules.Attachments.Contracts;
using Helpdesk.Modules.Attachments.Persistence;
using Helpdesk.Modules.Attachments.Services;
using Helpdesk.SharedKernel.Database;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Helpdesk.Modules.Attachments;

public static class AttachmentsModule
{
    public const int MigrationOrder = 50;

    /// <summary>
    /// Registers attachment validation/metadata and the object-store seam. <c>Storage:Provider</c> picks <c>InMemory</c> or
    /// <c>S3</c> (any S3-compatible endpoint); the application never depends on a vendor.
    /// </summary>
    public static IServiceCollection AddAttachmentsModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddModuleDbContext<AttachmentsDbContext>(AttachmentsDbContext.Schema, "attachments", MigrationOrder);
        services.AddOptions<AttachmentOptions>().Bind(configuration.GetSection(AttachmentOptions.Section));
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.Section))
            .Validate(o => o.Provider is "InMemory" or "S3", "Storage:Provider must be InMemory or S3.")
            .Validate(
                o => o.Provider != "S3" || (!string.IsNullOrWhiteSpace(o.S3.Bucket) && !string.IsNullOrWhiteSpace(o.S3.AccessKey) && !string.IsNullOrWhiteSpace(o.S3.SecretKey)),
                "Storage:S3 needs Bucket, AccessKey and SecretKey (set the keys through user-secrets or environment).")
            .ValidateOnStart();

        var provider = configuration.GetSection(StorageOptions.Section)[nameof(StorageOptions.Provider)] ?? "InMemory";
        if (provider == "S3")
        {
            services.TryAddSingleton<IObjectStore, S3ObjectStore>();
        }
        else
        {
            services.TryAddSingleton<InMemoryObjectStore>();
            services.TryAddSingleton<IObjectStore>(sp => sp.GetRequiredService<InMemoryObjectStore>());
        }

        services.TryAddSingleton<IAttachmentScanner, NoScanner>();
        services.AddScoped<IAttachmentStore, AttachmentStoreService>();
        return services;
    }
}
