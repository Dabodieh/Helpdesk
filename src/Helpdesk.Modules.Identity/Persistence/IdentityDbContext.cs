using Helpdesk.Modules.Identity.Domain;
using Helpdesk.SharedKernel.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Helpdesk.Modules.Identity.Persistence;

internal sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public const string Schema = "identity";

    public DbSet<User> Users => Set<User>();

    public DbSet<ExternalIdentity> ExternalIdentities => Set<ExternalIdentity>();

    public DbSet<DataProtectionKeyRow> DataProtectionKeys => Set<DataProtectionKeyRow>();

    public DbSet<EntraSettings> EntraSettings => Set<EntraSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<DataProtectionKeyRow>(e =>
        {
            e.ToTable("data_protection_keys");
            e.HasKey(x => x.FriendlyName);
            e.Property(x => x.FriendlyName).HasColumnName("friendly_name").HasMaxLength(200);
            e.Property(x => x.Xml).HasColumnName("xml");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<EntraSettings>(e =>
        {
            e.ToTable("entra_settings", t => t.HasCheckConstraint("ck_entra_settings_single_row", "id = 1"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id").HasMaxLength(64);
            e.Property(x => x.ClientId).HasColumnName("client_id").HasMaxLength(64);
            e.Property(x => x.ClientSecretProtected).HasColumnName("client_secret_protected");
            e.Property(x => x.SecretExpiresAt).HasColumnName("secret_expires_at");
            e.Property(x => x.ApplicationObjectId).HasColumnName("application_object_id").HasMaxLength(64);
            e.Property(x => x.RedirectOrigins).HasColumnName("redirect_origins").HasMaxLength(2000);
            e.Property(x => x.BootstrapAdminSubject).HasColumnName("bootstrap_admin_subject").HasMaxLength(200);
            e.Property(x => x.ConfiguredBy).HasColumnName("configured_by");
            e.Property(x => x.ConfiguredAt).HasColumnName("configured_at");
        });

        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(200);
            e.Property(x => x.Email).HasColumnName("email").HasMaxLength(320);
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.IsPlatformAdmin).HasColumnName("is_platform_admin");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.LastSignInAt).HasColumnName("last_sign_in_at");
            e.HasIndex(x => x.IsPlatformAdmin).HasDatabaseName("ix_users_is_platform_admin");
        });

        modelBuilder.Entity<ExternalIdentity>(e =>
        {
            e.ToTable("external_identities");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.Provider).HasColumnName("provider").HasMaxLength(32);
            e.Property(x => x.IssuerTenant).HasColumnName("issuer_tenant").HasMaxLength(64);
            e.Property(x => x.Subject).HasColumnName("subject").HasMaxLength(200);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasOne(x => x.User).WithMany(x => x.ExternalIdentities).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.Provider, x.IssuerTenant, x.Subject }).IsUnique().HasDatabaseName("ux_external_identities_provider_tenant_subject");
            e.HasIndex(x => x.UserId).HasDatabaseName("ix_external_identities_user_id");
        });
    }
}

internal sealed class IdentityDesignTimeFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextExtensions.DesignTimeOptions<IdentityDbContext>(IdentityDbContext.Schema));
}
