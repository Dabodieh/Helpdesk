-- DEV / DEMO DATA ONLY. Removes the demonstration department created by scripts/dev-demo-seed.mjs and nothing else:
--   * the department with key DEMO and every row that belongs to it (any table with a department_id column, audit rows included: this is a throw-away dev database),
--   * the demo-* sign-in users (subject like 'demo-%') together with their memberships, followers, notifications and links.
-- The existing "IT Support" department (key IT), other departments and dev-admin are never touched.
--
--   docker exec -i helpdesk-postgres-1 psql -U helpdesk -d helpdesk < scripts/dev-demo-reset.sql
--
-- Needs a superuser (the local dev role is one): foreign keys are not enforced while the delete runs, so the order does not matter and the audit table's
-- immutability trigger does not fire. Do not run this against anything but the local development database.
BEGIN;
SET LOCAL session_replication_role = replica;

DO $$
DECLARE
    dept uuid;
    demo_users uuid[];
    r record;
BEGIN
    SELECT id INTO dept FROM organisation.departments WHERE key = 'DEMO';
    SELECT coalesce(array_agg(DISTINCT user_id), '{}') INTO demo_users FROM identity.external_identities WHERE subject LIKE 'demo-%';

    IF dept IS NOT NULL THEN
        -- Mailbox-keyed rows have no department column: collect them before the mailboxes go.
        DELETE FROM mail.mailbox_sync WHERE mailbox_id IN (SELECT id FROM mail.mailboxes WHERE department_id = dept);
        DELETE FROM mail.notifications WHERE mailbox_id IN (SELECT id FROM mail.mailboxes WHERE department_id = dept);
        FOR r IN
            SELECT table_schema AS s, table_name AS t FROM information_schema.columns
            WHERE column_name = 'department_id' AND table_schema IN ('ticketing', 'mail', 'knowledge', 'attachments', 'organisation', 'audit')
              AND NOT (table_schema = 'organisation' AND table_name = 'departments')
        LOOP
            EXECUTE format('DELETE FROM %I.%I WHERE department_id = $1', r.s, r.t) USING dept;
        END LOOP;
        DELETE FROM organisation.departments WHERE id = dept;
    END IF;

    IF cardinality(demo_users) > 0 THEN
        FOR r IN
            SELECT table_schema AS s, table_name AS t FROM information_schema.columns
            WHERE column_name = 'user_id' AND table_schema IN ('ticketing', 'organisation', 'knowledge')
        LOOP
            EXECUTE format('DELETE FROM %I.%I WHERE user_id = ANY($1)', r.s, r.t) USING demo_users;
        END LOOP;
        DELETE FROM identity.external_identities WHERE user_id = ANY(demo_users);
        DELETE FROM identity.users WHERE id = ANY(demo_users);
    END IF;
END $$;

COMMIT;
