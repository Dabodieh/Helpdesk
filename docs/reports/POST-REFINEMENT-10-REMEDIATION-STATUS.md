# Post-Refinement-10 remediation status

Short tracker. Findings and decisions: `POST-REFINEMENT-10-MASTER-REPORT.md`, `../plans/POST-REFINEMENT-10-DECISIONS.md`. Test runs are targeted only; no full suites. SDK note: .NET 10 runs in the `hd-sdk` container.

| WS | Status | Findings closed | Commits | Externally blocked / open | Tests run |
|----|--------|-----------------|---------|---------------------------|-----------|
| 1 Administrative isolation | Complete | SEC-001, SEC-005, SEC-012, ARC-002, QA-007 | c5b9d36 | - | (earlier) |
| 2 Ticket parties | Complete | MSG-002, SEC-006, MSG-003, BE-001, SEC-004, BE-002, ARC-003 | 368c683 | MSG-012 (token wording) open | (earlier) |
| 3 Mail delivery state | Complete (MSG-007 deferred) | MSG-001, BE-009, ARC-001, MSG-006, MSG-008, MSG-010 | a42d010 | MSG-007 forged bounce needs real headers (E-38, D-16); MSG-006 header shape needs E-37/E-38 | GraphDeliveryConfidenceTests, MailInboundTests (duplicate), EmailIdentityTests, QueuePreferencesTests, mail.test.tsx; migration DeliveryStatesUncertainBounced up/down/up |
| 4 Mailbox registration | Complete (tenant evidence open) | MSG-004 (code) | see git log | D-16 E-2/E-3 scoping proof | MailAdminApiTests, GraphProviderTests, mail.test.tsx |
