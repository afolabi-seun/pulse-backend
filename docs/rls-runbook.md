# Row-level security: deployment and operations runbook

Pulse uses Postgres row-level security (RLS) as a second line of defence behind the application's own access checks
(`ProjectAccessPolicy`). The tenant tables run `FORCE ROW LEVEL SECURITY`, and the policies read who is asking from
per-connection session variables that `RlsConnectionInterceptor` sets on every connection.

RLS only does anything if the application connects as the right kind of role. This runbook covers how to set that up,
how migrations and background work fit in, what changes with multi-tenancy, and how to check it is really on.

## What RLS does and does not exempt

`FORCE ROW LEVEL SECURITY` applies the policies to the table **owner** as well. The only roles that skip RLS are a
**superuser** and any role with the **`BYPASSRLS`** attribute.

So there are two separate questions for any connection:

1. Is the role exempt (superuser or `BYPASSRLS`)? Then no policy ever filters it.
2. If not exempt, what identity does it carry? The policies read `app.current_role`, `app.current_user_id` (and, with
   multi-tenancy, `app.current_org_id`). An empty or missing identity matches nothing and the connection sees **no rows**.

Both mistakes have the same quiet symptom, and neither raises an error.

| Mistake | What happens |
|---|---|
| Requests run as a superuser or owner-with-`BYPASSRLS` | RLS never filters. It looks fine and protects nothing. |
| A non-exempt connection with no identity (for example a migration) | Every row is filtered out. An `UPDATE` "succeeds" having touched zero rows. |

## The two-role model

| Role | Superuser / `BYPASSRLS` | Used by | Why |
|---|---|---|---|
| `pulse_owner` | both **false** | startup migrations, Hangfire storage, demo seeding (`DB_MIGRATION_CONNECTION`) | Needs DDL and table ownership. Owns the tables and the `hangfire` schema. |
| `pulse_rls_app` | both **false** | every request (`DB_CONNECTION`) | Not an owner, so the policies apply to its queries. |

Neither role is exempt from RLS. The owner's connection sees every row because it **identifies as the `service` role**
(next section), not because the role is privileged. `app.is_global_role()` treats `service` as unrestricted.

> Do not put the owner in `DB_CONNECTION`. Requests would then run with owner privileges and no longer exercise the
> restricted role.

## Identities

| Who | `app.current_role` | `app.current_user_id` | `app.current_org_id` |
|---|---|---|---|
| An authenticated request | the caller's role | the caller's id | the caller's organization (once the `org_id` claim exists) |
| Background jobs (no HTTP request) | `service` | empty | empty |
| The startup migration connection | `service` | empty | empty |
| Demo seeding | `service` | empty | empty |
| An unauthenticated request | empty | empty | empty (only RLS-exempt tables, such as auth, are reachable) |

The migration and demo-seeding connections take their identity from `ServiceIdentityConnectionInterceptor`, which sets
the three session variables directly. It deliberately does **not** go through `IRlsContext`, so it does not change shape
when that interface gains an organization id. It clears `app.current_org_id` explicitly so a pooled connection that last
served a tenant's request cannot leak that tenant into a migration.

## Multi-tenancy

Pulse is moving to one database serving many customer organizations (see `docs/design/multi-tenancy-and-billing.md`).

Where it stands:

- **Phase 0 (merged):** the `organizations` table, a seeded default organization, and an `organization_id` column on
  teams, engineers and projects. Nothing reads it yet, and the tables carry no organization policy.
- **Phase 1 (in progress):** tighten the column to `NOT NULL`, add `app.current_org_id()`, extend the RLS context with an
  organization id. Phase 1d adds the organization backstop policies.
- **Phase 2:** the `org_id` JWT claim and the first real second organization in production.

What to hold on to as that lands:

- **The `service` identity is cross-organization on purpose.** Migrations, background jobs and demo seeding must see every
  tenant. Any organization policy has to let `service` through (as `app.is_global_role()` does today), or the migration
  fix described below stops working.
- **A tenant request must never run as `service`.** `RlsServiceOverride` exists for one narrow case (the dev-only demo
  endpoints) and must not be applied to anything user-facing.
- **Mind the recursion trap.** A policy function that reads another FORCE-RLS table re-enters that table's own policy and
  recurses ("stack depth limit exceeded"). `FixCanAccessProjectRecursion` was exactly that. An organization lookup inside
  `app.can_access_project()` needs the same care, and it must not require a superuser to create.
- **Cross-organization tests need two real organizations.** The demo seeder seeds one. See *Demo endpoints*.

## Production setup

1. **Provision the runtime role.** Run `docs/rls-runtime-role.sql` as the owner or a superuser, in psql or any SQL
   client. Edit the password literal first (it appears twice) and confirm the database name. Run it **after** the
   migrations have applied, because the `app` schema must exist. The last statement should show `rolsuper = f` and
   `rolbypassrls = f`. Store the password in your secret manager.
2. **Split the connection strings.**
   - `DB_CONNECTION` is the `pulse_rls_app` credentials (requests).
   - `DB_MIGRATION_CONNECTION` is the `pulse_owner` credentials (migrations, Hangfire, demo seeding).

   If `DB_MIGRATION_CONNECTION` is unset it falls back to `DB_CONNECTION`, which is fine for a single-role local setup
   and for the integration tests, and means RLS does nothing.
3. **Set the environment correctly.** `ASPNETCORE_ENVIRONMENT=Production` on any shared server, and never set
   `ENABLE_DEMO_ENDPOINTS` there. In Development the API exposes Swagger, returns exception messages and stack traces,
   and logs every SQL statement. The app logs a critical warning at startup if it is in Development against a
   non-local database.
4. **Deploy and restart.** Startup migrations run as the owner; requests run as `pulse_rls_app`.
5. **Verify** (below).

## Migrations and RLS

The migration context identifies as `service` (`ApplyMigrationsAsync`), so a data-changing migration sees every row of
every organization, without the owner role needing `BYPASSRLS`. `MigrationRlsTests` proves both halves: with no identity
a non-bypass role updates 0 rows, and as the service identity it updates the row. A third test checks the identity is
the service role with no user and no organization.

Rules for writing and running data fixes:

- **Spot-check the rows after any data migration on a tenant table.** A green pipeline and an entry in
  `__EFMigrationsHistory` do not prove it changed anything.
- **A one-off fix run by hand as the owner needs the identity too.** Start the session with
  `select set_config('app.current_role', 'service', false);` or the `UPDATE` is filtered to zero rows.
- A migration that takes `LOCK TABLE` and toggles `NO FORCE ROW LEVEL SECURITY` around a backfill (as
  `AddProjectCodeAndTaskNumber` did) is a workaround for the same problem. Prefer relying on the service identity.

## Demo endpoints

`POST /api/v1/demo/reset` and `/clear` wipe **every row**, take no login, and are therefore guarded three ways:

1. `ASPNETCORE_ENVIRONMENT=Development`, **and**
2. `ENABLE_DEMO_ENDPOINTS=true` (set by the `dotnet run` launch profiles and the local compose file, never on a server),
   **and**
3. the database holds **at most one organization**. With more, they return 409, because a wipe would take every tenant's
   data.

The seeder runs on the owner connection with the service identity: `TRUNCATE ... RESTART IDENTITY` needs ownership of
the sequences, which the restricted runtime role does not have.

Heads-up for Phase 1 testing: seeding two organizations to test cross-organization isolation needs seeder support for a
second organization, and reset will refuse once one exists. Decide how that test data is created before it is needed.

## Local development

`docker-compose.yml` at the project root runs the same two roles so RLS applies locally:
`docker/postgres-init/01-roles-and-database.sql` creates `pulse_owner` and `pulse_rls_app` on an empty data
directory, and the API gets `DB_CONNECTION` and `DB_MIGRATION_CONNECTION` to match. (Those two files sit at the project
root, outside the backend repository.) If you had the stack up before the split, run `docker compose down -v` once, because
the init script only runs on an empty data directory.

Locally the runtime role is also granted `TRUNCATE` by default privileges as a convenience. Production's is not.

## Verification

Prove RLS is active for the runtime role. Connect as `pulse_rls_app` (or `set role pulse_rls_app` from the owner):

```sql
-- No identity: sees nothing.
select count(*) from tasks;                                       -- 0

-- As the service identity: sees everything.
select set_config('app.current_role', 'service', false);
select count(*) from tasks;                                       -- every row

-- As an engineer who is not a member of a project:
select set_config('app.current_role', 'engineer', false),
       set_config('app.current_user_id', '<engineer-uuid>', false);
select count(*) from tasks;                                       -- excludes non-member projects
```

Then check the roles themselves, and which role requests really use:

```sql
select rolname, rolsuper, rolbypassrls from pg_roles where rolname in ('pulse_owner', 'pulse_rls_app');
-- every value must be f

select usename, count(*) from pg_stat_activity where datname = current_database() group by 1;
-- requests should show pulse_rls_app; migrations/Hangfire pulse_owner
```

If `rolsuper` or `rolbypassrls` is true for the runtime role, RLS is being bypassed and the setup is wrong. Through the
app, an engineer requesting a task outside their projects should get `403` (application layer), and a raw query under
their identity should return zero rows (RLS).

## Rollback

- **App:** point `DB_CONNECTION` back at the owner role and redeploy. Behaviour reverts to application-layer enforcement
  only, with RLS no longer filtering requests.
- **Schema:** `dotnet ef database update <migration-before-AddRowLevelSecurity>` (the migration's `Down` drops the
  policies, `FORCE`, and the `app` schema).

## What is covered

FORCE RLS with policies on `projects`, `tasks`, `epics`, `wiki_pages`, `task_comments`, `task_history`,
`task_estimation_sessions`, `task_estimation_votes`, `task_dependencies`, `wiki_page_revisions` and `sprints`, plus the
org-wide read-only roles and personal-project rules added later. `project_members` deliberately has no RLS, because the
policies read it and it would recurse. There is **no organization policy yet**; until Phase 1d, tenant separation rests
on the application layer.
