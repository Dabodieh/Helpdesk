-- Development -> Production configuration sync (apply step). Idempotent; one transaction (BEGIN comes from the loader, COMMIT/ROLLBACK from the caller).
-- Prerequisite: temp tables s_dep, s_tt, s_bh loaded with COPY from the Development database (departments, ticket_types, business_hours).
-- Departments match by lower(key) (Production values win; never duplicated); ticket types and business hours by (department, name). Users, tickets, mailboxes,
-- personal canned responses, saved views and teams are NOT copied. Run only after a verified Production backup. See PRODUCTION_INTEGRATION_VALIDATION.md section 12.
-- blockers: abort the whole transaction on anything that cannot be resolved safely
DO $$
DECLARE n int;
BEGIN
  SELECT count(*) INTO n FROM s_dep s JOIN organisation.departments d ON lower(d.name)=lower(s.name) AND lower(d.key)<>lower(s.key);
  IF n>0 THEN RAISE EXCEPTION 'blocker: % department name(s) clash with a different Production key', n; END IF;
  SELECT count(*) INTO n FROM s_dep s JOIN organisation.departments d ON d.id=s.id AND lower(d.key)<>lower(s.key);
  IF n>0 THEN RAISE EXCEPTION 'blocker: % department id(s) already used by another key', n; END IF;
  SELECT count(*) INTO n FROM s_tt s WHERE EXISTS (SELECT 1 FROM ticketing.ticket_types t WHERE t.id=s.id);
  IF n>0 AND NOT EXISTS (SELECT 1 FROM ticketing.ticket_types) THEN RAISE EXCEPTION 'blocker: ticket type ids clash'; END IF;
END $$;

CREATE TEMP TABLE c_new ON COMMIT DROP AS
SELECT s.* FROM s_dep s WHERE NOT EXISTS (SELECT 1 FROM organisation.departments d WHERE lower(d.key)=lower(s.key));
INSERT INTO organisation.departments (id,key,name,description,is_active,created_at,updated_at)
SELECT id,key,name,description,is_active,created_at,now() FROM c_new;

CREATE TEMP TABLE dmap ON COMMIT DROP AS
SELECT s.id src_id, d.id dst_id, d.key FROM s_dep s JOIN organisation.departments d ON lower(d.key)=lower(s.key);

CREATE TEMP TABLE c_tt ON COMMIT DROP AS
SELECT s.*, m.dst_id dst_department FROM s_tt s JOIN dmap m ON m.src_id=s.department_id
WHERE NOT EXISTS (SELECT 1 FROM ticketing.ticket_types t WHERE t.department_id=m.dst_id AND t.name=s.name);
INSERT INTO ticketing.ticket_types (id,department_id,name,description,is_active,is_default,sort_order,created_at,updated_at)
SELECT c.id, c.dst_department, c.name, c.description, c.is_active,
       c.is_default AND NOT EXISTS (SELECT 1 FROM ticketing.ticket_types x WHERE x.department_id=c.dst_department AND x.is_default),
       c.sort_order, c.created_at, now()
FROM c_tt c;

CREATE TEMP TABLE c_bh ON COMMIT DROP AS
SELECT s.*, m.dst_id dst_department FROM s_bh s JOIN dmap m ON m.src_id=s.department_id
WHERE NOT EXISTS (SELECT 1 FROM ticketing.business_hours b WHERE b.department_id=m.dst_id AND b.name=s.name);
INSERT INTO ticketing.business_hours (id,department_id,name,time_zone,is_active,weekly,exceptions,created_at,updated_at)
SELECT id,dst_department,name,time_zone,is_active,weekly,exceptions,created_at,now() FROM c_bh;

-- audit trail: one event per created department and one summary (append only; nothing existing is touched)
INSERT INTO audit.audit_events (id,occurred_at,category,action,object_type,object_id,department_id,previous,next,source)
SELECT gen_random_uuid(), now(), 'organisation', 'department.created', 'department', id, id, NULL,
       jsonb_build_object('key',key,'name',name), 'config-sync' FROM c_new;
INSERT INTO audit.audit_events (id,occurred_at,category,action,object_type,object_id,department_id,previous,next,source)
SELECT gen_random_uuid(), now(), 'organisation', 'config.synchronised', 'configuration', NULL, NULL, NULL,
       jsonb_build_object('departments',(SELECT count(*) FROM c_new),'ticketTypes',(SELECT count(*) FROM c_tt),'businessHours',(SELECT count(*) FROM c_bh),'from','development'), 'config-sync'
WHERE (SELECT count(*) FROM c_new)+(SELECT count(*) FROM c_tt)+(SELECT count(*) FROM c_bh) > 0;

SELECT 'departments created' k,(SELECT count(*) FROM c_new) v UNION ALL
SELECT 'departments matched existing',(SELECT count(*) FROM s_dep)-(SELECT count(*) FROM c_new) UNION ALL
SELECT 'ticket types created',(SELECT count(*) FROM c_tt) UNION ALL
SELECT 'ticket types already present/skipped',(SELECT count(*) FROM s_tt)-(SELECT count(*) FROM c_tt) UNION ALL
SELECT 'business hours created',(SELECT count(*) FROM c_bh);
