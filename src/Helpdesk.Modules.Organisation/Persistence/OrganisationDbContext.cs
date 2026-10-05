using Helpdesk.Modules.Organisation.Domain;
using Helpdesk.SharedKernel.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Helpdesk.Modules.Organisation.Persistence;

internal sealed class OrganisationDbContext(DbContextOptions<OrganisationDbContext> options) : DbContext(options)
{
    public const string Schema = "organisation";

    public DbSet<Department> Departments => Set<Department>();

    public DbSet<Team> Teams => Set<Team>();

    public DbSet<DepartmentMembership> DepartmentMemberships => Set<DepartmentMembership>();

    public DbSet<TeamMembership> TeamMemberships => Set<TeamMembership>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Department>(e =>
        {
            e.ToTable("departments", t =>
            {
                t.HasCheckConstraint("ck_departments_key_format", "key ~ '^[A-Z][A-Z0-9]{1,9}$'");
                t.HasCheckConstraint("ck_departments_name_not_blank", "length(btrim(name)) > 0");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.Key).HasColumnName("key").HasMaxLength(10);
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(100);
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(500);
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            // Unique indexes on lower(key) / lower(name) are created in the migration (expression indexes).
        });

        modelBuilder.Entity<Team>(e =>
        {
            e.ToTable("teams", t => t.HasCheckConstraint("ck_teams_name_not_blank", "length(btrim(name)) > 0"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(100);
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(500);
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            e.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            // Composite FK target for team_memberships.
            e.HasAlternateKey(x => new { x.Id, x.DepartmentId }).HasName("ux_teams_id_department");
            e.HasIndex(x => x.DepartmentId).HasDatabaseName("ix_teams_department_id");
        });

        modelBuilder.Entity<DepartmentMembership>(e =>
        {
            e.ToTable("department_memberships", t =>
                t.HasCheckConstraint("ck_department_memberships_role", "role IN ('DepartmentAdmin','TeamLead','Agent','Viewer')"));
            e.HasKey(x => new { x.UserId, x.DepartmentId });
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.Role).HasColumnName("role").HasMaxLength(32);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.DepartmentId).HasDatabaseName("ix_department_memberships_department_id");
            // user_id -> identity.users(id) is a cross-schema foreign key added in migration SQL.
        });

        modelBuilder.Entity<TeamMembership>(e =>
        {
            e.ToTable("team_memberships");
            e.HasKey(x => new { x.TeamId, x.UserId });
            e.Property(x => x.TeamId).HasColumnName("team_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasOne<Team>().WithMany().HasForeignKey(x => new { x.TeamId, x.DepartmentId }).HasPrincipalKey(t => new { t.Id, t.DepartmentId })
                .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_team_memberships_team_department");
            e.HasOne<DepartmentMembership>().WithMany().HasForeignKey(x => new { x.UserId, x.DepartmentId })
                .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_team_memberships_department_membership");
            e.HasIndex(x => x.UserId).HasDatabaseName("ix_team_memberships_user_id");
        });
    }
}

internal sealed class OrganisationDesignTimeFactory : IDesignTimeDbContextFactory<OrganisationDbContext>
{
    public OrganisationDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextExtensions.DesignTimeOptions<OrganisationDbContext>(OrganisationDbContext.Schema));
}
