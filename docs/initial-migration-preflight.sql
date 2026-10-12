DO $preflight$
BEGIN
    IF current_database() <> 'sportstats_dev'
       OR current_user <> 'sportstats_migrator'
       OR session_user <> 'sportstats_migrator'
       OR inet_server_addr() IS DISTINCT FROM '127.0.0.1'::inet
       OR inet_server_port() IS DISTINCT FROM 5432
       OR current_setting('TimeZone') <> 'UTC'
       OR (SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = current_database()) <> 'sportstats_migrator'
    THEN RAISE EXCEPTION 'Migration target identity, owner, endpoint or timezone mismatch'; END IF;
    IF EXISTS (SELECT 1 FROM pg_namespace WHERE nspname IN ('platform', 'football'))
       OR EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                  WHERE n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'
                    AND c.relkind IN ('r', 'p', 'f'))
    THEN RAISE EXCEPTION 'Expected an empty database with no platform or football schema'; END IF;
    RAISE NOTICE 'Preflight passed: sportstats_dev, sportstats_migrator, 127.0.0.1:5432, UTC, empty target';
END
$preflight$;
