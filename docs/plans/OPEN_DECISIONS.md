# Open Decisions

Resolved defaults are marked; items needing product-owner input are marked **PO**.

| ID | Decision | Default taken | Status |
|---|---|---|---|
| D-01 | Single Entra tenant or multi-tenant sign-in | Single tenant (configured tenant id) | Default; **PO** to confirm |
| D-02 | Can PlatformAdmin read tickets in all departments? | No: requires explicit (audited) membership. Alternative: implicit read-all | **PO** (affects privacy posture) |
| D-03 | Teams as visibility boundary | No in v1 (assignment/queue grouping only) | Default; **PO** to confirm |
| D-04 | PostgreSQL RLS as extra isolation layer | Decide after Phase 1 spike; scoped EF filters are mandatory regardless | Open (Principal) |
| D-05 | Requester identity per department vs global | Per department (isolation) | Decided |
| D-06 | Ticket numbering format | `KEY-n` per department | Decided |
| D-07 | Shared mailbox vs user mailbox for department mailboxes | Shared mailboxes assumed | **PO** to confirm tenant setup |
| D-08 | Attachment AV scanning | Hook + scan-state field; engine (e.g. ClamAV) chosen in Phase 3 | Open |
| D-09 | Retention/deletion policy for tickets and mail | None in v1; document before production | **PO** |
| D-10 | Hosting target (on-prem Docker vs Azure) | Docker/Linux agnostic | **PO** |
