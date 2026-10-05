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

No `HasQueryFilter` is introduced even now — RLS remains the backstop and `ProjectAccessPolicy` the primary gate, consistent with the existing pattern, rather than adding a third enforcement point in EF Core.

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

**Phase 1 — App-layer checks.** Tighten the new columns to `NOT NULL`. Extend `RlsConnectionInterceptor` and the Postgres policy functions with `app.current_org_id`. Extend `ProjectAccessPolicy`. Introduce `ICurrentUserService` and point the load-bearing call sites at it.

**Phase 2 — Multi-org live.** Add the `org_id` JWT claim. Build org creation and invite flows. This is the phase where a second organization's data first exists in production — everything before it is invisible plumbing.

**Phase 3 — Billing.** `Plan` entity, provider webhook endpoint, seat metering, enforcement on plan limits (detailed in Billing model above).

## Open questions & risks

- **`Project.Code` uniqueness.** It's documented as a unique prefix string today — globally unique, or per-org? Almost certainly needs to become per-org (so two orgs can both have a project coded "ENG"), which is a uniqueness-constraint change beyond just adding a column.
- **RLS recursion risk.** A past migration (`FixCanAccessProjectRecursion`) had to repair a recursive-query problem in `app.can_access_project()`. Adding another join for `organization_id` into that same function family needs a query-plan check, not just a correctness check — easy to reintroduce a similar issue.
- **The \~90 non-load-bearing controller helpers.** Phase 1 only repoints the \~80 `ProjectAccessPolicy` call sites and `HttpRlsContext` at the new `ICurrentUserService`. The remaining ad-hoc claim readers are deliberately left alone for now — worth a follow-up decision on whether they migrate opportunistically or stay as-is indefinitely.
- **Demo/seed data.** `DemoSeeder` currently seeds one organization's worth of personas. Testing cross-org isolation needs at least two seeded orgs with overlapping role names, so a bug that leaks across orgs is actually visible in the demo environment rather than only in production.
- **Payment provider.** Phase 3 is blocked on choosing one (Stripe vs. Paddle vs. other) — out of scope for this doc, but it's the one phase that can't start from this design alone.
