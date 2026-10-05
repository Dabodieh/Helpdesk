# ADR-002: PostgreSQL 18 as the single datastore

Status: Accepted (2026-10-05)

## Context
Relational data with strong integrity needs; FTS initially; jobs storage.

## Decision
PostgreSQL 18, one database, schema per module, EF Core/Npgsql, UUIDv7 keys, timestamptz UTC, Hangfire PostgreSQL storage, full-text search via tsvector. Attachment binaries live in object storage, never in PostgreSQL.

## Alternatives considered
SQL Server (licensing), separate search engine (premature).

## Consequences
+ One system to run/back up. - FTS limits revisited under R-09; RLS option kept open (D-04).
