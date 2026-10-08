# SLA 80% alert backfill

One-off operator procedure. Tickets whose SLA was created before the 80% alert existed have no alert instant, so they alert only at breach until their SLA is next recalculated. The backfill gives their running clocks an alert instant, using the same business-hours, exception and pause calculation as the SLA engine.

It is **never** scheduled and **never** runs on startup or deployment. It is a Host command that runs once and exits, like `migrate`.

## What it does and does not do
- Touches only running clocks (deadline set; not met, paused or breached; no alert fields yet). Completed, paused and merge-stopped clocks are skipped.
- Future 80% point: stores the alert time; the SLA monitor fires it as normal.
- 80% point already passed: marks the clock as alerted **without a notification** (no retrospective alerts). A breach is still reported as normal.
- Writes only `fr_alert_at`, `fr_alerted_at`, `res_alert_at`, `res_alerted_at` and `next_event_at` on `ticketing.ticket_sla`. Deadlines, warnings, breaches, SLA events, automation and existing notifications are untouched.
- Batched (200 rows per round, keyset on ticket id), idempotent, safe to rerun or to interrupt.

## Prerequisites
1. The migrations `20261008160000_SlaAlertNotifications` and `20261008170000_SlaAlertInstants` are applied (`... run --rm migrate`). Without them the command fails.
2. A database backup taken just before `--apply` (`scripts/ops/backup-database.sh`, see `BACKUP_AND_RESTORE.md`).
3. Run outside the busiest hours; it takes a row lock per updated ticket only for the duration of one statement.

## 1. Dry run (writes nothing)
```
docker compose -f docker/production/compose.yml --env-file docker/production/.env run --rm --no-deps helpdesk sla-alert-backfill
```
The log line reports `dry run`, the number of tickets, alerts that would be scheduled and clocks already past 80% (would be marked silently). Check the numbers are plausible against the count of open tickets with an SLA.

## 2. Apply
Same command with `--apply`. The log line reports `applied` with the same three figures.

## 3. Verify
- Run the dry run again: it must report 0 tickets, 0 scheduled, 0 already past.
- Optional SQL (read-only): running clocks still missing an alert instant should be none.
  ```sql
  SELECT count(*) FROM ticketing.ticket_sla
  WHERE (res_due_at IS NOT NULL AND res_met_at IS NULL AND res_paused_at IS NULL AND res_breached_at IS NULL AND res_target_minutes IS NOT NULL
         AND res_alert_at IS NULL AND res_alerted_at IS NULL)
     OR (fr_due_at IS NOT NULL AND fr_met_at IS NULL AND fr_breached_at IS NULL AND fr_target_minutes IS NOT NULL
         AND fr_alert_at IS NULL AND fr_alerted_at IS NULL);
  ```
- Over the next hours, `sla_approaching` notifications appear for assigned agents as clocks cross 80%; no burst of alerts appears immediately after the run.

## Recovery
- **Interrupted run:** rerun it; rows already done are skipped.
- **Wrong result suspected:** the alert columns are derived data. Any later recalculation of a ticket's SLA (status, team, priority change) recomputes the alert instant for a clock that has not alerted yet, and `next_event_at` heals the same way, so a stray early `next_event_at` only makes the monitor look at the row sooner.
- **Full rollback:** restore the pre-apply backup into a new database (`BACKUP_AND_RESTORE.md`). Down-migrations are not the rollback path (`PRODUCTION_DEPLOYMENT.md`). Restoring loses everything written after the backup, so prefer rerunning or the derived-data note above for anything short of corruption.

## Evidence
`SlaAlertBackfillTests` covers dry run writes nothing, apply counts, deadlines and SLA events unchanged, paused clock skipped, idempotent rerun, no retrospective alert, scheduled alert fires later. It ran against a throw-away test database only; the command has not been run against production.
