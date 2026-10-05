# Pulse Multi-Tenancy & Billing Design

Oct 5, 2026 · @Seun Afolabi

## Goals & scope

This doc designs how Pulse moves from one organization per deployment to **one shared instance serving multiple organizations**, the tenancy model already agreed on, and lays out the billing integration points that model unlocks.

In scope:

- An `Organization` entity and how it attaches to every existing tenant-scoped table (users, teams, projects, tasks, sprints, etc.)
- How access control (today's `ProjectAccessPolicy` and role system) extends to enforce org boundaries as the outermost scope
- How auth (JWT, session) carries and resolves the caller's organization
- The billing model's shape — plans, metering, where a payment provider plugs in — not a specific provider integration
- A phased rollout plan from the current single-tenant data to multi-tenant

Out of scope (deferred):

- Picking a specific payment provider (Stripe vs. Paddle vs. other) — this doc defines the integration points, not the vendor
- Per-tenant data isolation stronger than row-level scoping (separate databases or schemas per org) — row-level scoping within one shared database/schema is the chosen model
- Self-serve signup / org creation UX — covered once the data model underneath it exists
- Regional data residency requirements

## Current state

Pulse has zero partial org scaffolding today — not even a stub column. Every layer assumes exactly one organization per deployment.

| Layer | Today | Gap for multi-tenancy |
| --- | --- | --- |
| Data model | `PulseDbContext` registers 36 `DbSet<T>`s across 31 domain entities; no `HasQueryFilter` exists anywhere (not even for soft-delete) | No `OrganizationId`/`TenantId` column on any table |
| Postgres RLS | `RlsConnectionInterceptor` stamps `app.current_role` + `app.current_user_id` as session vars on every connection open; policies on `projects`/`tasks`/`epics`/`wiki_pages` key on `app.can_access_project(id)` | No `app.current_org_id` session var; a policy that passes the project/role check today has no org boundary to also check |
| App-layer authorization | `ProjectAccessPolicy` (`CanAccessProjectAsync`, `CanAccessTeamAsync`, etc.) is called ad hoc inside \~80+ MediatR handlers; no `IPipelineBehavior` or middleware wraps requests — confirmed zero `IPipelineBehavior` registrations anywhere | An org check added the same ad-hoc way would need the same \~80+ call sites touched individually, and is easy to miss on a new handler |
| Auth / JWT | `JwtService.GenerateAccessToken` puts only `sub`, `email`, `ClaimTypes.Role`, `jti` into the token; claims are read back per-controller via local helpers (no central `ICurrentUserService`) | The caller's org isn't known from the token — nothing exists to seed an `app.current_org_id` session var with until this changes |
| Roles | `Roles.*` are plain string constants on `Engineer` (not an enum); `PermissionService.For(role)` maps a role string to a permission set, additive by tier (Engineer ⊂ TeamLead ⊂ … ⊂ HeadOfRnD) | Orthogonal to tenancy — role tiers should keep working unchanged *within* an org |

The existing RLS doc comments already frame the two layers as defense-in-depth: `ProjectAccessPolicy` is "the primary, fine-grained gate," RLS is "the coarse hard backstop." That's the pattern this doc extends for org-scoping, rather than inventing a third mechanism.

## Tenancy model

**Decision: one shared Postgres database, row-level org scoping.** Every org-scoped table gets an `OrganizationId` column; Postgres RLS and `ProjectAccessPolicy` both enforce it alongside the checks they already do. No separate database or schema per org.

| Model | Isolation strength | Ops burden | Migration effort from today | Fit |
| --- | --- | --- | --- | --- |
| **Row-level scoping (chosen)** | Good — enforced at both the Postgres RLS layer and the app layer, same defense-in-depth pattern already in place for projects | One DB, one set of migrations, one Hangfire instance — scales the way the app already runs today | Add a column + backfill + extend existing RLS policies; no new infra | Matches the already-agreed "one shared instance, multiple orgs" direction |
| Schema-per-tenant | Better — Postgres enforces isolation structurally | Migrations must run per schema; connection routing per request; N schemas to monitor | Requires a schema-router layer that doesn't exist today | Overkill for the agreed model; revisit only if a customer demands stronger isolation |
| Database-per-tenant | Best — physical isolation | Highest — a DB (and a Hangfire scheduler instance) per org, provisioning automation required | Full re-architecture of connection management and deployment | Rejected — contradicts the shared-instance decision and the app's current single-connection-string deployment model |

Row-level scoping also composes cleanly with the access model already in the codebase: `OrganizationId` becomes the outermost scope, with `OwnerTeamId`/`TeamId`/`PersonalOwnerId` continuing to narrow access *within* an org exactly as they do today. An org boundary that's crossed is a bug; a team/project boundary that's crossed within the same org is normal role-based access.

## Data model changes

**New `Organization` entity** (`Pulse.Domain/Organizations/Organization.cs`): `Id, Name, Slug, PlanId, BillingEmail, IsActive, CreatedAt`. `Slug` is the login/routing key (e.g. an org picker or subdomain later) and is unique.

Not every table needs its own `OrganizationId` column — only the tables that are roots of an ownership chain. Everything reachable through a foreign key inherits scope from its parent, which keeps the migration (and every future RLS policy) smaller:

| Entity | Gets a direct `OrganizationId` column? | Why |
| --- | --- | --- |
| `Team` | Yes | Root of the team/engineer tree; `Engineer.TeamId` is nullable, so `Team` can't be the only anchor |
| `Engineer` | Yes | `TeamId` is nullable (executives, HR, accountant roles have no team) — can't rely on inheriting through `Team` alone |
| `Project` | Yes | `OwnerTeamId` and `PersonalOwnerId` are both nullable — same reasoning as `Engineer` |
| `PulseTask`, `Epic`, `Sprint`, `WikiPage`, `TaskComment`, etc. | No | Always reachable via `ProjectId` → inherit `Project.OrganizationId` |
| `TimeEntry`, `CheckIn`, `VitalsResponse`, `Notification`, `AuditLog`, etc. | No | Always reachable via `EngineerId` → inherit `Engineer.OrganizationId` |

Migration mechanics, in order:

1. Create the `organizations` table; seed exactly one row ("default org") so every existing row has somewhere to point
2. Add `OrganizationId` to `Team`, `Engineer`, `Project` as nullable, backfill every existing row to the default org's id, then tighten to `NOT NULL` with a FK
3. Extend `RlsConnectionInterceptor` to also stamp `app.current_org_id`; extend the Postgres functions from the RLS migrations (`app.can_access_project`, etc.) to additionally check `organization_id = app.current_org_id()` — directly on `teams`/`engineers`/`projects`, via a join through `project_id`/`engineer_id` everywhere else
4. Extend `ProjectAccessPolicy`'s methods to also compare the resource's (inherited) `OrganizationId` against the actor's — same defense-in-depth pairing as the project-level checks today

**Revised in Phase 1c (Oct 5, 2026): org scoping uses EF Core global query filters.** The original plan avoided `HasQueryFilter` on the assumption that `ProjectAccessPolicy` gates every read, but list queries never go through it (see *Phase 1 scope findings*). Filters on all 39 entity types (`PulseDbContext.OrganizationFilters.cs`) scope every query to the caller's org: directly where a table has `organization_id`, otherwise through its parent, with nested filters keeping each chain inside one org. They apply only to authenticated callers; background jobs and anonymous endpoints (login) are unfiltered until 1e. New rows an authenticated caller adds are stamped with the caller's org on `SaveChanges`. RLS (1d) remains the database backstop, and `ProjectAccessPolicy` still decides access *within* an org.

## Access & auth changes

**JWT**: `JwtService.GenerateAccessToken` adds one claim — `org_id` — alongside the existing `sub`, `email`, `ClaimTypes.Role`, `jti`. `GenerateServiceToken` is untouched; service-to-service calls stay org-agnostic and exempt from RLS, as they are today.

**A new, narrow `ICurrentUserService`.** Today, org/role/user-id claims are read back via \~170 ad-hoc per-controller helpers (e.g. `TasksController.GetActorId()`), and `HttpRlsContext.Resolve()` duplicates that parsing again for the RLS session vars. Adding a new claim on top of that pattern is exactly how a call site quietly keeps using the old, org-blind behavior. Rather than touching every call site, introduce one small service — `UserId`, `Role`, `OrganizationId` off the current `HttpContext.User` — and point `HttpRlsContext` and the \~80 `ProjectAccessPolicy` call sites at it. The other \~90 controller helpers that only ever read actor id (not role or org) can migrate opportunistically; they aren't load-bearing for org isolation.

**`ProjectAccessPolicy`**: each method (`CanAccessProjectAsync`, `CanAccessTeamAsync`, etc.) adds an org check — resolve the resource's (possibly inherited) `OrganizationId` and compare it to the caller's before running today's role/ownership logic. A mismatch returns "not found," not "forbidden" — consistent with how cross-tenant resources should be invisible, not merely blocked.

**The isolation guarantee this buys**: a request authenticated for org A can never read or write a row whose resolved `OrganizationId` isn't A, enforced at two independent layers — Postgres RLS (`app.current_org_id`, infra-layer, fails closed even if application code has a bug) and `ProjectAccessPolicy` (app-layer, the primary day-to-day gate). This mirrors the project-level defense-in-depth that already exists; org becomes the outermost ring around it, not a replacement for it.

## Billing model

Two new entities, kept separate so the plan catalog and an org's actual contract can change independently:

| Entity | Holds | Notes |
| --- | --- | --- |
| `Plan` | `Name, SeatLimit, PriceMonthly, Features` | The catalog — Free / Pro / Enterprise. `Organization.PlanId` (added in Data model changes) points here |
| `Subscription` | `OrganizationId, ProviderCustomerId, ProviderSubscriptionId, Status, CurrentPeriodEnd` | The org's actual contract with whichever payment provider is chosen; provider ids are opaque strings so swapping providers later doesn't touch the domain model |

**Metering unit: active seats.** Pulse is a per-seat team tool, so the natural metered quantity is `Engineer` rows with `IsActive = true` inside an org — no new usage-tracking concept is needed, the existing active/inactive flag already used for deactivation (`UsersPage`, `DeleteUser`) is the meter. A Hangfire job (Hangfire is already in use for scheduled work) syncs the seat count to the provider nightly; `CreateUserCommand` and user-reactivation both check the seat count against `Plan.SeatLimit` before allowing it, the same place that would show "Set a discipline first" style validation today.

**Provider integration points** (the provider itself is deliberately not picked here):

- `IBillingProvider` in `Pulse.Application` — `CreateCustomer`, `CreateSubscription`, `UpdateSeatCount`, `CancelSubscription` — one concrete implementation per provider lives in `Pulse.Infrastructure`, swappable the same way other external integrations in this codebase are abstracted
- A webhook endpoint (new controller) for provider-initiated events — subscription updated, payment failed, cancelled — authenticated by the provider's own signature scheme, not JWT; structurally closer to `GenerateServiceToken`'s service-to-service path than to a user-facing endpoint
- **Failure mode**: a payment failure flips `Organization.IsActive = false` after the provider's own grace period, via the webhook. Every engineer in that org is locked out at login — reusing the lockout plumbing `Engineer` already has (`IsActive`, `LockedUntil`), just checked one level up, at the org

## Rollout plan

```
 Schema + RLS ──▶ App layer ──▶ Multi-org live ──▶ Billing
 additive only   org-aware      orgs + invites     plans +
                 gates                             metering
      │               │               │
      ▼               ▼               ▼
 backfill        isolation       2nd org live
 verified        tests green     in prod
```

Each phase is additive and gated on proof the previous one actually holds before the next starts.

**Phase 0 — Schema & RLS (additive only).** Add the `organizations` table, seed one default org, add nullable `OrganizationId` to `Team`/`Engineer`/`Project`, backfill every row to it. No RLS or application behavior changes yet — the column exists but nothing reads it.

**Phase 1 — Org isolation.** Split into five independently shippable steps, because the audit at the start of Phase 1 found the isolation surface is wider than `ProjectAccessPolicy` and RLS alone (see *Phase 1 scope findings* below). Nothing is user-visible until Phase 2 creates a second org, so each step merges on its own.

| Step | Scope |
| --- | --- |
| **1a — Org context** | `org_id` JWT claim (moved here from Phase 2: RLS needs it); `ICurrentUserService`; `HttpRlsContext` and `RlsConnectionInterceptor` stamp `app.current_org_id`; `app.current_org_id()` SQL function; `organization_id` tightened to `NOT NULL` |
| **1b — Schema completion** | `OrganizationId` on org-wide config tables with no path to an org; per-org instead of global uniqueness |
| **1c — Query scoping** | Org filter on every list/aggregate repository method; org check in `ProjectAccessPolicy`, including the global-role and department-head short-circuits; reports |
| **1d — RLS backstop** | Org predicate in every RLS policy, and RLS on `engineers`/`teams` with an explicit service path for login |
| **1e — Background jobs** | The recurring Hangfire jobs iterate per organization and use that org's thresholds |

Alongside every step: a two-org isolation test suite (orgs A and B with overlapping role names), asserting org B sees none of org A's data. Each step extends it.

**Phase 2 — Multi-org live.** Build org creation and invite flows; drop the `organization_id` column default and pass the creator's org into `Team`/`Engineer`/`Project.Create`. This is the phase where a second organization's data first exists in production — everything before it is invisible plumbing.

### Phase 1 scope findings

Recorded at the start of Phase 1 (Oct 5, 2026) from the code as it stood:

- **List queries bypass `ProjectAccessPolicy`.** The policy answers "can this actor open resource X"; ~30 repository list methods (all active engineers, all projects, all teams, …) are unscoped and feed list pages and reports. `GlobalRoles` also return `true` before any lookup. Org isolation needs a filter at the query level, not only a policy check.
- **Tables with no ownership chain to an org.** `threshold_settings`, `department_overwork_thresholds`, `google_chat_spaces` and `failed_emails` reach neither `Project` nor `Engineer`, so they get their own `OrganizationId` (done in 1b; the two threshold tables are now keyed `(organization_id, key/department)`). On closer inspection `alert_rules` and `audit_log` inherit through their non-null owner/actor engineer, and `alert_metric_snapshots` through its team or project scope, so they need no column.
- **App-level uniqueness checks are still app-wide after 1b.** The database now enforces team name and project code per org, but the application's own pre-checks (e.g. `ProjectCodeGenerator`) still look across all orgs. That's stricter, not unsafe; 1c scopes them so a second org can actually reuse a name.
- **Mapping Google Chat spaces to an org.** A space is registered when the bot is added to it, from Google's callback, which carries no Pulse org. Phase 2 needs a way to link a space to the org that installed it.
- **Globally unique keys.** `teams.name`, `projects.code`, `engineers.email` and `google_chat_spaces.space_id` are unique app-wide. Team name and project code become per-org. **Decision (Oct 5, 2026): `engineers.email` stays globally unique — one account per email, belonging to exactly one org.** Login is unchanged (the email identifies the org); multi-org membership can be added later as its own feature.
- **Background jobs.** Nine recurring Hangfire jobs run as the RLS `service` role across all data, using global thresholds.
  - *Resolved in 1e:* each schedule runs `OrganizationJobRunner<TJob>`, which fans out one child Hangfire job per active organization. A child resolves the job in a fresh DI scope whose `BackgroundOrganizationContext` is that org, so the EF filters, the RLS org stamp, new-row stamping and per-org thresholds all follow, without the jobs knowing about orgs. Each org's run retries on its own, so a failure never re-sends to orgs that already succeeded. The runner is generic on the class, not its methods, because Hangfire can't restore generic methods; a unit test pins that round trip. Failed emails are filed under the recipient's org. Ad-hoc jobs (`SendEmailJob`, Slack and Google Chat reply handlers) still run unscoped.
- **In-memory thresholds were app-wide.** `OverworkThresholds` was a process-wide singleton, hydrated at startup and mutated in place on update, so one org's admin changing thresholds would have changed them for every org. Fixed in 1c: a per-org cache (`OrganizationThresholdsCache`) resolved per request, with `OverworkSignalsCalculator` now scoped.
- **`engineers` and `teams` have no RLS.** Only projects, tasks, epics, wiki pages, sprints and task children do. Adding it to `engineers` touches the unauthenticated login path.
  - *Resolved in 1d:* every policy keys on `app.org_visible(org)`, which allows all orgs when no org is stamped on the connection (background jobs, and anonymous requests such as login) and otherwise only the stamped org. An authenticated caller without an org is stamped `Guid.Empty` and sees nothing. Tables that already had RLS get an extra **restrictive** org policy, so `app.can_access_project()` and its siblings are untouched. 25 tables get RLS for the first time, with org-only policies. A project's org is read from `project_organizations`, a trigger-maintained mirror with no RLS, so the tasks policy doesn't pick up projects' membership rule. Deliberately left without RLS (EF filters only): `subtasks`, `escalation_events`, `automation_executions`, `sprint_retrospectives`, because their parents' RLS encodes access rules beyond org. A test fails if any other table lacks RLS.
- **Runtime DB role grants — resolved.** `docs/rls-runtime-role.sql` (added in #6) grants `pulse_rls_app` default privileges on every table `pulse_owner` creates, so `organizations` and later tables are covered without per-migration grants. `app.current_org_id()` is executable by default (Postgres grants `EXECUTE` on functions to `PUBLIC`).

**Phase 3 — Billing.** `Plan` entity, provider webhook endpoint, seat metering, enforcement on plan limits (detailed in Billing model above).

## Open questions & risks

- **`Project.Code` uniqueness.** It's documented as a unique prefix string today — globally unique, or per-org? Almost certainly needs to become per-org (so two orgs can both have a project coded "ENG"), which is a uniqueness-constraint change beyond just adding a column.
- **RLS recursion risk.** A past migration (`FixCanAccessProjectRecursion`) had to repair a recursive-query problem in `app.can_access_project()`. Adding another join for `organization_id` into that same function family needs a query-plan check, not just a correctness check — easy to reintroduce a similar issue.
- **The \~90 non-load-bearing controller helpers.** Phase 1 only repoints the \~80 `ProjectAccessPolicy` call sites and `HttpRlsContext` at the new `ICurrentUserService`. The remaining ad-hoc claim readers are deliberately left alone for now — worth a follow-up decision on whether they migrate opportunistically or stay as-is indefinitely.
- **Demo/seed data.** `DemoSeeder` currently seeds one organization's worth of personas. Testing cross-org isolation needs at least two seeded orgs with overlapping role names, so a bug that leaks across orgs is actually visible in the demo environment rather than only in production.
- **Payment provider.** Phase 3 is blocked on choosing one (Stripe vs. Paddle vs. other) — out of scope for this doc, but it's the one phase that can't start from this design alone.
