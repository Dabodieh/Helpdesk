-- Production database roles (step C of the pre-production plan). Run once as the PostgreSQL administrator role, connected to the Production database,
-- AFTER the data was restored with `pg_restore --no-owner` (objects are then owned by the administrator, who also runs migrations):
--
--   psql -U <admin> -d helpdesk_prod -v ON_ERROR_STOP=1 -v app_user=helpdesk_app -v app_password=<from .env> -f scripts/ops/prod-db-roles.sql
--
-- The application connects as the least-privilege role: it can read and write rows (and use sequences) in the module schemas and nothing else. It cannot create,
-- alter or drop objects, truncate, or administer roles; schema changes happen only through the explicit `migrate` run as the administrator.
-- Idempotent: safe to run again. Never prints or stores the password.

select format('create role %I login nosuperuser nocreatedb nocreaterole noinherit password %L', :'app_user', :'app_password')
where not exists (select 1 from pg_roles where rolname = :'app_user') \gexec

select format('alter role %I password %L', :'app_user', :'app_password')
where exists (select 1 from pg_roles where rolname = :'app_user') \gexec

select format('revoke all on database %I from public', current_database()) \gexec
select format('grant connect on database %I to %I', current_database(), :'app_user') \gexec

-- Every non-system schema: usage, row access, sequence use, and the same for objects the administrator creates later (migrations).
select format('grant usage on schema %I to %I', nspname, :'app_user')
from pg_namespace where nspname not like 'pg\_%' and nspname <> 'information_schema' \gexec

select format('grant select, insert, update, delete on all tables in schema %I to %I', nspname, :'app_user')
from pg_namespace where nspname not like 'pg\_%' and nspname <> 'information_schema' \gexec

select format('grant usage, select, update on all sequences in schema %I to %I', nspname, :'app_user')
from pg_namespace where nspname not like 'pg\_%' and nspname <> 'information_schema' \gexec

select format('alter default privileges for role %I in schema %I grant select, insert, update, delete on tables to %I', current_user, nspname, :'app_user')
from pg_namespace where nspname not like 'pg\_%' and nspname <> 'information_schema' \gexec

select format('alter default privileges for role %I in schema %I grant usage, select, update on sequences to %I', current_user, nspname, :'app_user')
from pg_namespace where nspname not like 'pg\_%' and nspname <> 'information_schema' \gexec
