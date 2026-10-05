# ADR-001: Modular monolith

Status: Accepted (2026-10-05)

## Context
Small technical team; need strong boundaries without operational overhead.

## Decision
One deployable host; modules as separate projects with contract-only dependencies and module-owned DB schemas; boundaries enforced by architecture tests.

## Alternatives considered
Microservices (operational cost, distributed transactions); single-project layered monolith (boundaries erode).

## Consequences
+ Simple ops, transactional integrity, extractable later. - Requires discipline and arch tests; shared DB demands schema ownership rules.
