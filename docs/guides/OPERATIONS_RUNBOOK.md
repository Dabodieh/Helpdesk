# Operations runbook

## Probes
- `/health/live`: the process is up.
- `/health/ready`: PostgreSQL reachable, the object store reachable, and every module's migrations applied (decision D4). A job backlog does NOT make the instance unready; use the metrics below.

## Metrics (OpenTelemetry, meter `Helpdesk.Operations`; exported when `OTEL_EXPORTER_OTLP_ENDPOINT` is set, together with traces and logs)
Sampled every 30 seconds. Counts and ages only; they never carry content, addresses or identifiers.

| Metric | Alert when | Meaning |
|--------|-----------|---------|
| `helpdesk_notification_outbox_oldest_age_seconds` | above 300 for 10 min | the notification drain is stuck or one item keeps failing |
| `helpdesk_mail_outbound_oldest_queued_age_seconds` | above 900 | replies are not leaving (dispatcher, provider or mailbox problem) |
| `helpdesk_mail_sync_oldest_age_seconds` | above 1800 | an active mailbox has not synchronised (stale webhook AND delta reconciliation) |
| `helpdesk_mail_outbound_needing_attention` | above 0 for 1 h | Uncertain or Failed outbound mail waits for an operator (Mail needing attention) |
| `helpdesk_jobs_failed` | rising | failed background jobs (read-only dashboard `/hangfire`, Platform Admin) |
| `helpdesk_job_{mail_sync,mail_dispatch,sla_monitor,notification_drain}_lag_seconds` | above 300 | that recurring job has stopped running (jobs server down or locked) |

The thresholds are starting points, not approved service levels. Logs are structured (Serilog) on stdout and, with the OTLP endpoint set, also exported centrally. Log templates carry no addresses, subjects, bodies or tokens (enforced by `Log_templates_never_carry_...`).

## When a dependency is down
**PostgreSQL.** Everything fails; readiness fails. Restart/repair the database; the application reconnects. Nothing is lost that was committed. If the database is lost, restore (`BACKUP_AND_RESTORE.md`).

**Object storage.** Readiness fails (503), so the proxy/orchestrator can stop sending traffic if it is configured to. Ticket text still works if the instance stays in rotation; uploading and opening attachments fails. Inbound mail whose attachments cannot be stored is retried by the mail loop (state Failed with retry); nothing is dropped. After recovery, check Mail needing attention.

**Microsoft Graph / Exchange.** Inbound: webhooks and the 5-minute delta reconciliation stop; `helpdesk_mail_sync_oldest_age_seconds` grows; mail is picked up from the last delta token on recovery (no loss, `not-before` window applies only to a first sync). Outbound: transient errors are retried with backoff (5 attempts), then Failed (retry from the ticket). A timeout after submission becomes Uncertain, is checked once more automatically after 10 minutes, and otherwise waits for an administrator (check the mailbox Sent Items first). Replies are never sent from another mailbox.

**Entra ID.** Nobody can sign in; existing sessions keep working until they expire.

## Routine
Backups hourly; restore drill quarterly (`BACKUP_AND_RESTORE.md`). Logs and operational tables are not pruned yet: retention periods are provisional (D7/D-09) and pruning will not be switched on until the organisation's policy approves them. Watch table growth until then.
