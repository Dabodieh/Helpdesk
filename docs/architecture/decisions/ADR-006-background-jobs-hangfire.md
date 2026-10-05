# ADR-006: Hangfire for background jobs

Status: Accepted (2026-10-05)

## Context
Need retry-safe jobs for mail, SLA timers, automation with low operational cost.

## Decision
Hangfire with PostgreSQL storage, hosted in the same image with ROLE=web|worker|all. Jobs are idempotent, run as System principal, dashboard restricted to PlatformAdmin. Business logic sits behind our own interfaces so Hangfire stays replaceable.

## Alternatives considered
Quartz.NET, custom hosted services, message broker (more infrastructure).

## Consequences
+ Retries, dashboard, minimal infra. - Job state in the main database; revisit on evidence (R-10).
