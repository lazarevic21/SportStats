-- Read-only audit. Connect securely to sportstats_dev using psql -X -w.
-- No password values or PostgreSQL password catalogs are queried.
BEGIN TRANSACTION READ ONLY;

SELECT current_database(), current_user, current_setting('TimeZone') AS timezone;

SELECT r.rolname, r.rolcanlogin, r.rolsuper, r.rolcreatedb, r.rolcreaterole,
       r.rolreplication, r.rolbypassrls, r.rolinherit,
       has_database_privilege(r.oid, current_database(), 'CONNECT') AS can_connect,
       has_database_privilege(r.oid, current_database(), 'CREATE') AS can_create_schema,
       has_database_privilege(r.oid, current_database(), 'TEMPORARY') AS can_create_temp,
       pg_get_userbyid(d.datdba) AS database_owner
FROM pg_roles r CROSS JOIN pg_database d
WHERE r.rolname IN ('sportstats_migrator', 'sportstats_app')
  AND d.datname = current_database();

SELECT r.rolname, wanted.schema_name, pg_get_userbyid(n.nspowner) AS schema_owner,
       n.oid IS NOT NULL AS schema_exists,
       has_schema_privilege(r.oid, n.oid, 'USAGE') AS can_use,
       has_schema_privilege(r.oid, n.oid, 'CREATE') AS can_create
FROM pg_roles r CROSS JOIN (VALUES ('platform'), ('football'), ('public')) wanted(schema_name)
LEFT JOIN pg_namespace n ON n.nspname = wanted.schema_name
WHERE r.rolname IN ('sportstats_migrator', 'sportstats_app');

-- Includes inherited and SET ROLE access, not just directly granted membership.
SELECT member.rolname AS member_role, owner.rolname AS accessible_role,
       pg_has_role(member.oid, owner.oid, 'MEMBER') AS is_member,
       pg_has_role(member.oid, owner.oid, 'USAGE') AS inherited_access,
       pg_has_role(member.oid, owner.oid, 'SET') AS can_set_role
FROM pg_roles member CROSS JOIN pg_roles owner
WHERE member.rolname IN ('sportstats_migrator', 'sportstats_app')
  AND member.oid <> owner.oid
  AND (pg_has_role(member.oid, owner.oid, 'MEMBER')
       OR pg_has_role(member.oid, owner.oid, 'USAGE')
       OR pg_has_role(member.oid, owner.oid, 'SET'));

SELECT r.rolname, n.nspname, c.relname, pg_get_userbyid(c.relowner) AS table_owner,
       has_table_privilege(r.oid, c.oid, 'SELECT') AS can_select,
       has_table_privilege(r.oid, c.oid, 'INSERT') AS can_insert,
       has_table_privilege(r.oid, c.oid, 'UPDATE') AS can_update,
       has_table_privilege(r.oid, c.oid, 'DELETE') AS can_delete,
       has_table_privilege(r.oid, c.oid, 'TRUNCATE') AS can_truncate,
       has_table_privilege(r.oid, c.oid, 'REFERENCES') AS can_reference,
       has_table_privilege(r.oid, c.oid, 'TRIGGER') AS can_trigger
FROM pg_roles r CROSS JOIN pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE r.rolname IN ('sportstats_migrator', 'sportstats_app')
  AND n.nspname IN ('platform', 'football') AND c.relkind IN ('r', 'p', 'v', 'm');

-- Inspect global as well as schema-specific defaults; include PUBLIC grant sources.
SELECT pg_get_userbyid(a.defaclrole) AS creating_role, n.nspname AS schema_name,
       a.defaclobjtype, a.defaclacl
FROM pg_default_acl a LEFT JOIN pg_namespace n ON n.oid = a.defaclnamespace
WHERE a.defaclnamespace = 0 OR n.nspname IN ('platform', 'football');

SELECT datname, datacl FROM pg_database WHERE datname = current_database();
SELECT nspname, nspacl FROM pg_namespace WHERE nspname IN ('platform', 'football', 'public');
COMMIT;
