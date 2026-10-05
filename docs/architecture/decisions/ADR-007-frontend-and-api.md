# ADR-007: Frontend and API conventions

Status: Accepted (2026-10-05)

## Context
Agent workspace is the main surface; strong typing across the boundary.

## Decision
React 19 + TS + Vite + React Router + TanStack Query + Zod. REST + OpenAPI; TS API types generated from the OpenAPI document; RFC 9457 problem details; Zod validates inbound data. Frontend never enforces permissions.

## Alternatives considered
GraphQL (not needed), Next.js SSR (no SEO need, extra runtime).

## Consequences
+ Single typed contract. - Generator step in the build.
