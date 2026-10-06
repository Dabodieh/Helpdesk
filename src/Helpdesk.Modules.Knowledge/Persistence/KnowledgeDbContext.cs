using Helpdesk.Modules.Knowledge.Domain;
using Helpdesk.SharedKernel.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Helpdesk.Modules.Knowledge.Persistence;

internal sealed class KnowledgeDbContext(DbContextOptions<KnowledgeDbContext> options) : DbContext(options)
{
    public const string Schema = "knowledge";

    public DbSet<KnowledgeCategory> Categories => Set<KnowledgeCategory>();

    public DbSet<KnowledgeArticle> Articles => Set<KnowledgeArticle>();

    public DbSet<ArticleFeedback> Feedback => Set<ArticleFeedback>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<KnowledgeCategory>(e =>
        {
            e.ToTable("categories", t => t.HasCheckConstraint("ck_categories_name", "length(btrim(name)) BETWEEN 1 AND 100"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(100);
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(500);
            e.Property(x => x.SortOrder).HasColumnName("sort_order");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            e.HasAlternateKey(x => new { x.Id, x.DepartmentId }).HasName("ux_categories_id_department");
            e.HasIndex(x => new { x.DepartmentId, x.Name }).IsUnique().HasDatabaseName("ux_categories_department_name");
        });

        modelBuilder.Entity<KnowledgeArticle>(e =>
        {
            e.ToTable("articles", t =>
            {
                t.HasCheckConstraint("ck_articles_status", "status IN ('Draft','Published','Archived')");
                t.HasCheckConstraint("ck_articles_visibility", "visibility IN ('Everyone','Requesters')");
                t.HasCheckConstraint("ck_articles_slug", "slug ~ '^[a-z0-9][a-z0-9-]{0,79}$'");
                // A published or archived article has published content; a draft never does.
                t.HasCheckConstraint("ck_articles_published_shape", "(status = 'Draft' AND published_at IS NULL) OR (status IN ('Published','Archived') AND published_at IS NOT NULL AND published_title IS NOT NULL AND published_body IS NOT NULL)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.CategoryId).HasColumnName("category_id");
            e.Property(x => x.Slug).HasColumnName("slug").HasMaxLength(80);
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(16);
            e.Property(x => x.Visibility).HasColumnName("visibility").HasMaxLength(16);
            e.Property(x => x.DraftTitle).HasColumnName("draft_title").HasMaxLength(200);
            e.Property(x => x.DraftSummary).HasColumnName("draft_summary").HasMaxLength(500);
            e.Property(x => x.DraftBody).HasColumnName("draft_body");
            e.Property(x => x.DraftUpdatedAt).HasColumnName("draft_updated_at");
            e.Property(x => x.DraftUpdatedByUserId).HasColumnName("draft_updated_by");
            e.Property(x => x.PublishedTitle).HasColumnName("published_title").HasMaxLength(200);
            e.Property(x => x.PublishedSummary).HasColumnName("published_summary").HasMaxLength(500);
            e.Property(x => x.PublishedBody).HasColumnName("published_body");
            e.Property(x => x.PublishedAt).HasColumnName("published_at");
            e.Property(x => x.PublishedByUserId).HasColumnName("published_by");
            e.Property(x => x.AuthorUserId).HasColumnName("author_user_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            e.HasOne<KnowledgeCategory>().WithMany().HasForeignKey(x => new { x.CategoryId, x.DepartmentId })
                .HasPrincipalKey(c => new { c.Id, c.DepartmentId }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_articles_category_department");

            // Full-text search over the published slot only (generated and indexed by PostgreSQL).
            e.Property(x => x.SearchVector).HasColumnName("search_vector");
            e.HasGeneratedTsVectorColumn(x => x.SearchVector, "english", x => new { x.PublishedTitle, x.PublishedSummary, x.PublishedBody });
            e.HasIndex(x => x.SearchVector).HasMethod("GIN").HasDatabaseName("ix_articles_search");
            e.HasIndex(x => x.Slug).IsUnique().HasDatabaseName("ux_articles_slug");
            e.HasIndex(x => new { x.DepartmentId, x.Status, x.UpdatedAt }).IsDescending(false, false, true).HasDatabaseName("ix_articles_department_status");
            e.HasIndex(x => new { x.PublishedAt, x.Id }).IsDescending(true, true).HasFilter("status = 'Published'").HasDatabaseName("ix_articles_published");
            e.HasIndex(x => x.CategoryId).HasDatabaseName("ix_articles_category");
        });

        modelBuilder.Entity<ArticleFeedback>(e =>
        {
            e.ToTable("article_feedback");
            e.HasKey(x => new { x.ArticleId, x.UserId });
            e.Property(x => x.ArticleId).HasColumnName("article_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.Helpful).HasColumnName("helpful");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasOne<KnowledgeArticle>().WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_feedback_article");
        });
    }
}

internal sealed class KnowledgeDesignTimeFactory : IDesignTimeDbContextFactory<KnowledgeDbContext>
{
    public KnowledgeDbContext CreateDbContext(string[] args) => new(ModuleDbContextExtensions.DesignTimeOptions<KnowledgeDbContext>(KnowledgeDbContext.Schema));
}
