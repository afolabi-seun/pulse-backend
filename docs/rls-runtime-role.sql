-- ============================================================================
-- Pulse — provision the non-superuser RUNTIME database role for RLS.
--
-- WHY: row-level security (FORCE ROW LEVEL SECURITY) applies to the table OWNER too; only a superuser, or a role with the
-- BYPASSRLS attribute, is exempt. The app's request path must connect as a dedicated NOSUPERUSER / NOBYPASSRLS, non-owner
-- role for the policies to filter anything. See docs/rls-runbook.md.
--
-- ROLE SPLIT (for this database the owner is `pulse_owner`):
--   * pulse_owner    - OWNER: owns the tables, runs migrations, Hangfire and demo seeding (DB_MIGRATION_CONNECTION). It is NOT
--                      exempt from RLS either; its migrations see every row because the migration connection identifies as the
--                      'service' role (ServiceIdentityConnectionInterceptor), not because the role is privileged.
--   * pulse_rls_app  - NEW limited role this script creates. The app's request path (DB_CONNECTION).
--
-- Don't confuse the two: pulse_owner is the owner, pulse_rls_app is the restricted runtime role. Putting the owner in
-- DB_CONNECTION would mean requests run with owner privileges and no longer exercise the restricted role.
--
-- This script is plain SQL — run it in psql, DBeaver, pgAdmin, etc., as the OWNER
-- (pulse_owner) or a superuser, AFTER the schema migrations have been applied
-- (the `app` schema and functions must already exist).
--
-- ┌─ BEFORE RUNNING: replace the password literal below (used twice). ─────────┐
-- │  Generate one with:  openssl rand -base64 24                              │
-- │  Store it in your secret manager — it becomes the password in the app's   │
-- │  DB_CONNECTION.                                                            │
-- └───────────────────────────────────────────────────────────────────────────┘
-- ============================================================================

-- 1. The runtime role. NOBYPASSRLS is the line that makes RLS apply to it.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pulse_rls_app') THEN
        CREATE ROLE pulse_rls_app LOGIN PASSWORD 'CHANGE_ME_STRONG'
            NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
    ELSE
        ALTER ROLE pulse_rls_app WITH LOGIN PASSWORD 'CHANGE_ME_STRONG'
            NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
    END IF;
END
$$;

-- 2. Connect + schema usage. The `app` schema is created by the RLS migration.
GRANT CONNECT ON DATABASE pulse TO pulse_rls_app;
GRANT USAGE ON SCHEMA public TO pulse_rls_app;
GRANT USAGE ON SCHEMA app TO pulse_rls_app;

-- 3. CRUD on every current application table — RLS still filters which ROWS are visible.
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO pulse_rls_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO pulse_rls_app;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA app TO pulse_rls_app;

-- 4. Future objects created by the owner in later migrations are auto-granted.
ALTER DEFAULT PRIVILEGES FOR ROLE pulse_owner IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO pulse_rls_app;
ALTER DEFAULT PRIVILEGES FOR ROLE pulse_owner IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO pulse_rls_app;
ALTER DEFAULT PRIVILEGES FOR ROLE pulse_owner IN SCHEMA app
    GRANT EXECUTE ON FUNCTIONS TO pulse_rls_app;

-- 5. Sanity check — both must be false for RLS to apply to this role.
SELECT rolname, rolsuper, rolbypassrls
FROM pg_roles
WHERE rolname = 'pulse_rls_app';
