-- CI copy of the local-dev init script (docker/postgres-init/01-roles-and-database.sql at the project root): the same owner + restricted
-- runtime role split production uses, so the smoke test runs the API the way production does. Local-only passwords.

CREATE ROLE pulse_owner   LOGIN PASSWORD 'ownerpassword' NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
CREATE ROLE pulse_rls_app LOGIN PASSWORD 'rlspassword'   NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;

CREATE DATABASE pulse_dev OWNER pulse_owner;

\connect pulse_dev

-- Everything the owner creates from now on (tables and sequences in migrations, the `app` and `hangfire` schemas) is usable by the
-- runtime role without a grant per migration. Row-level security still decides which ROWS it can see.
ALTER DEFAULT PRIVILEGES FOR ROLE pulse_owner GRANT USAGE ON SCHEMAS TO pulse_rls_app;
ALTER DEFAULT PRIVILEGES FOR ROLE pulse_owner IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO pulse_rls_app;
ALTER DEFAULT PRIVILEGES FOR ROLE pulse_owner IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO pulse_rls_app;

-- LOCAL ONLY: harmless safety net for tooling that truncates through the runtime role. (Demo reset itself runs on the owner connection.)
ALTER DEFAULT PRIVILEGES FOR ROLE pulse_owner IN SCHEMA public GRANT TRUNCATE ON TABLES TO pulse_rls_app;
