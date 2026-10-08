# Administration hub implementation

Scope: replace the directory landing with a searchable settings hub; rebuild Department Overview as a configuration control centre; improve context switching and direct links. Preserve Graphite tokens, React routes/components, permission helpers and existing query contracts.

Information architecture: global settings catalogue at /admin, dedicated directory at /admin/departments, existing Department routes with section and ticketing tab parameters. Global and Department settings stay visibly distinct. Acknowledgements get a direct Department section; all existing mail controls remain available.

No data model, backend API, security-policy or workflow changes. Status queries are enabled only for the selected, authorised Department and the required existing permission. Operational reporting requires canTicket, never the platform-admin flag. Search is local over authorised settings metadata, not ticket or requester content. Unavailable data stays unavailable rather than becoming a zero or healthy state.

Verification: focused administration/navigation/status frontend tests, TypeScript, both screens in both themes at 1366×768 and 1440×900. No Docker rebuild, data seeding or deployment. Rollback: revert the explicit frontend/documentation commit; no migration needed.
