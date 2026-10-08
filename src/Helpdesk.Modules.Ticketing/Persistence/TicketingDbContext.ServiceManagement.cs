using Helpdesk.Modules.Ticketing.Domain;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Persistence;

/// <summary>Phase 5 model: business hours, SLA policies and per-ticket SLA state, automation rules and their execution history.</summary>
internal sealed partial class TicketingDbContext
{
    public DbSet<BusinessHoursCalendar> BusinessHours => Set<BusinessHoursCalendar>();

    public DbSet<SlaPolicy> SlaPolicies => Set<SlaPolicy>();

    public DbSet<TicketSla> TicketSlas => Set<TicketSla>();

    public DbSet<SlaEvent> SlaEvents => Set<SlaEvent>();

    public DbSet<AutomationRule> AutomationRules => Set<AutomationRule>();

    public DbSet<AutomationExecution> AutomationExecutions => Set<AutomationExecution>();

    private void ConfigureServiceManagement(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BusinessHoursCalendar>(e =>
        {
            e.ToTable("business_hours", t => t.HasCheckConstraint("ck_business_hours_name", "length(btrim(name)) BETWEEN 1 AND 100"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(100);
            e.Property(x => x.TimeZoneId).HasColumnName("time_zone").HasMaxLength(100);
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.Weekly).HasColumnName("weekly").HasColumnType("jsonb");
            e.Property(x => x.Exceptions).HasColumnName("exceptions").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            e.HasAlternateKey(x => new { x.Id, x.DepartmentId }).HasName("ux_business_hours_id_department");
            e.HasIndex(x => new { x.DepartmentId, x.Name }).IsUnique().HasDatabaseName("ux_business_hours_department_name");
        });

        modelBuilder.Entity<SlaPolicy>(e =>
        {
            e.ToTable("sla_policies", t =>
            {
                t.HasCheckConstraint("ck_sla_policies_name", "length(btrim(name)) BETWEEN 1 AND 100");
                t.HasCheckConstraint("ck_sla_policies_targets", "(first_response_minutes IS NOT NULL OR resolution_minutes IS NOT NULL) AND COALESCE(first_response_minutes, 1) > 0 AND COALESCE(resolution_minutes, 1) > 0");
                t.HasCheckConstraint("ck_sla_policies_lead", "approaching_lead_minutes BETWEEN 0 AND 10080");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(100);
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.SortOrder).HasColumnName("sort_order");
            e.Property(x => x.Conditions).HasColumnName("conditions").HasColumnType("jsonb");
            e.Property(x => x.CalendarId).HasColumnName("calendar_id");
            e.Property(x => x.FirstResponseMinutes).HasColumnName("first_response_minutes");
            e.Property(x => x.ResolutionMinutes).HasColumnName("resolution_minutes");
            e.Property(x => x.PauseWhenPending).HasColumnName("pause_when_pending");
            e.Property(x => x.ApproachingLeadMinutes).HasColumnName("approaching_lead_minutes");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            e.HasOne<BusinessHoursCalendar>().WithMany().HasForeignKey(x => new { x.CalendarId, x.DepartmentId })
                .HasPrincipalKey(c => new { c.Id, c.DepartmentId }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sla_policies_calendar_department");
            e.HasIndex(x => new { x.DepartmentId, x.SortOrder }).HasDatabaseName("ix_sla_policies_department_order");
        });

        modelBuilder.Entity<TicketSla>(e =>
        {
            e.ToTable("ticket_sla");
            e.HasKey(x => x.TicketId);
            e.Property(x => x.TicketId).HasColumnName("ticket_id").ValueGeneratedNever();
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.PolicyId).HasColumnName("policy_id");
            e.Property(x => x.Snapshot).HasColumnName("snapshot").HasColumnType("jsonb");
            e.Property(x => x.AppliedAt).HasColumnName("applied_at");
            e.Property(x => x.ApproachingLeadMinutes).HasColumnName("approaching_lead_minutes");
            e.Property(x => x.FirstResponseTargetMinutes).HasColumnName("fr_target_minutes");
            e.Property(x => x.FirstResponseDueAt).HasColumnName("fr_due_at");
            e.Property(x => x.FirstResponseMetAt).HasColumnName("fr_met_at");
            e.Property(x => x.FirstResponseWarnedAt).HasColumnName("fr_warned_at");
            e.Property(x => x.FirstResponseBreachedAt).HasColumnName("fr_breached_at");
            e.Property(x => x.FirstResponseAlertAt).HasColumnName("fr_alert_at");
            e.Property(x => x.FirstResponseAlertedAt).HasColumnName("fr_alerted_at");
            e.Property(x => x.ResolutionTargetMinutes).HasColumnName("res_target_minutes");
            e.Property(x => x.ResolutionDueAt).HasColumnName("res_due_at");
            e.Property(x => x.ResolutionPausedAt).HasColumnName("res_paused_at");
            e.Property(x => x.ResolutionRemainingSeconds).HasColumnName("res_remaining_seconds");
            e.Property(x => x.ResolutionMetAt).HasColumnName("res_met_at");
            e.Property(x => x.ResolutionWarnedAt).HasColumnName("res_warned_at");
            e.Property(x => x.ResolutionBreachedAt).HasColumnName("res_breached_at");
            e.Property(x => x.ResolutionAlertAt).HasColumnName("res_alert_at");
            e.Property(x => x.ResolutionAlertedAt).HasColumnName("res_alerted_at");
            e.Property(x => x.ResolutionCycle).HasColumnName("res_cycle");
            e.Property(x => x.NextDueAt).HasColumnName("next_due_at");
            e.Property(x => x.NextWarnAt).HasColumnName("next_warn_at");
            e.Property(x => x.NextEventAt).HasColumnName("next_event_at");
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => new { x.TicketId, x.DepartmentId })
                .HasPrincipalKey(t => new { t.Id, t.DepartmentId }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_ticket_sla_ticket_department");

            // The monitor selects on next_event_at only; the queue filters read the two per-department summary columns.
            e.HasIndex(x => x.NextEventAt).HasFilter("next_event_at IS NOT NULL").HasDatabaseName("ix_ticket_sla_next_event");
            e.HasIndex(x => new { x.DepartmentId, x.NextDueAt }).HasFilter("next_due_at IS NOT NULL").HasDatabaseName("ix_ticket_sla_department_due");
            e.HasIndex(x => new { x.DepartmentId, x.NextWarnAt }).HasFilter("next_warn_at IS NOT NULL").HasDatabaseName("ix_ticket_sla_department_warn");
            e.HasQueryFilter(x => ScopedDepartmentIds.Contains(x.DepartmentId));
        });

        modelBuilder.Entity<SlaEvent>(e =>
        {
            e.ToTable("sla_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TicketId).HasColumnName("ticket_id");
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.At).HasColumnName("at");
            e.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(32);
            e.Property(x => x.Clock).HasColumnName("clock").HasMaxLength(16);
            e.Property(x => x.Detail).HasColumnName("detail").HasColumnType("jsonb");
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => new { x.TicketId, x.DepartmentId })
                .HasPrincipalKey(t => new { t.Id, t.DepartmentId }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sla_events_ticket_department");
            e.HasIndex(x => new { x.TicketId, x.At }).HasDatabaseName("ix_sla_events_ticket");
            e.HasQueryFilter(x => ScopedDepartmentIds.Contains(x.DepartmentId));
        });

        modelBuilder.Entity<AutomationRule>(e =>
        {
            e.ToTable("automation_rules", t => t.HasCheckConstraint("ck_automation_rules_name", "length(btrim(name)) BETWEEN 1 AND 100"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(100);
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.Trigger).HasColumnName("trigger").HasMaxLength(32);
            e.Property(x => x.SortOrder).HasColumnName("sort_order");
            e.Property(x => x.StopProcessing).HasColumnName("stop_processing");
            e.Property(x => x.Conditions).HasColumnName("conditions").HasColumnType("jsonb");
            e.Property(x => x.Actions).HasColumnName("actions").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            e.HasAlternateKey(x => new { x.Id, x.DepartmentId }).HasName("ux_automation_rules_id_department");
            e.HasIndex(x => new { x.DepartmentId, x.Trigger, x.SortOrder }).HasFilter("is_active").HasDatabaseName("ix_automation_rules_trigger");
        });

        modelBuilder.Entity<AutomationExecution>(e =>
        {
            e.ToTable("automation_executions", t => t.HasCheckConstraint("ck_automation_executions_state", "state IN ('Running','Completed','Failed','Skipped')"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.RuleId).HasColumnName("rule_id");
            e.Property(x => x.RuleName).HasColumnName("rule_name").HasMaxLength(100);
            e.Property(x => x.TicketId).HasColumnName("ticket_id");
            e.Property(x => x.TicketReference).HasColumnName("ticket_reference").HasMaxLength(32);
            e.Property(x => x.Trigger).HasColumnName("trigger").HasMaxLength(32);
            e.Property(x => x.TriggerKey).HasColumnName("trigger_key").HasMaxLength(200);
            e.Property(x => x.ChainId).HasColumnName("chain_id");
            e.Property(x => x.Depth).HasColumnName("depth");
            e.Property(x => x.Provenance).HasColumnName("provenance").HasMaxLength(32);
            e.Property(x => x.State).HasColumnName("state").HasMaxLength(16);
            e.Property(x => x.Matched).HasColumnName("matched");
            e.Property(x => x.Conditions).HasColumnName("conditions").HasColumnType("jsonb");
            e.Property(x => x.Actions).HasColumnName("actions").HasColumnType("jsonb");
            e.Property(x => x.Error).HasColumnName("error").HasMaxLength(500);
            e.Property(x => x.SkipReason).HasColumnName("skip_reason").HasMaxLength(64);
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.FinishedAt).HasColumnName("finished_at");
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => new { x.TicketId, x.DepartmentId })
                .HasPrincipalKey(t => new { t.Id, t.DepartmentId }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_automation_executions_ticket_department");
            e.HasOne<AutomationRule>().WithMany().HasForeignKey(x => new { x.RuleId, x.DepartmentId })
                .HasPrincipalKey(r => new { r.Id, r.DepartmentId }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_automation_executions_rule_department");

            // Idempotency: the same rule evaluates the same triggering event for the same ticket at most once.
            e.HasIndex(x => new { x.RuleId, x.TicketId, x.TriggerKey }).IsUnique().HasDatabaseName("ux_automation_executions_event");
            e.HasIndex(x => new { x.DepartmentId, x.StartedAt }).IsDescending(false, true).HasDatabaseName("ix_automation_executions_department");
            e.HasIndex(x => new { x.TicketId, x.StartedAt }).HasDatabaseName("ix_automation_executions_ticket");
        });
    }
}
