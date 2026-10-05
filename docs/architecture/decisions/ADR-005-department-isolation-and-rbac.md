# ADR-005: Department isolation and RBAC

Status: Accepted (2026-10-05)

## Context
Cross-department leakage is a critical defect.

## Decision
Department is the isolation boundary; department_id on all scoped rows; central IAuthorizer; 404 for inaccessible objects; scoped EF query filters; fixed system roles with permission codes in v1; PlatformAdmin does not implicitly read tickets (D-02, pending PO confirmation); teams not a security boundary (D-03). See PERMISSIONS.md.

## Alternatives considered
Separate database/schema per department (operationally heavy, hard cross-department features); frontend-only filtering (unacceptable).

## Consequences
+ Uniform, testable model. - Every scoped table carries department_id; cross-department features (move, reports) need explicit design.
