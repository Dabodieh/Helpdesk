# ADR-008: Serilog and OpenTelemetry

Status: Accepted (2026-10-05)

## Context
Failures must be observable; logs must not leak sensitive data.

## Decision
Serilog structured logging with destructuring denylist; OpenTelemetry traces/metrics/logs via configurable OTLP exporter; health endpoints live/ready; correlation id on requests, jobs and audit events.

## Alternatives considered
Default logging only.

## Consequences
+ Traceable email pipeline. - Redaction discipline needed.
