# Pulse — Backend

ASP.NET Core 8 Web API following Clean Architecture. Four projects plus two test projects, all under `src/backend/`.

```
src/backend/
├── Pulse.Api/             HTTP host — controllers, middleware, DI wiring
├── Pulse.Application/     Use cases — MediatR handlers, interfaces, overwork calculator, escalation scanner
├── Pulse.Domain/          Business rules — entities, value objects, DomainException
├── Pulse.Infrastructure/  Data + external — EF Core, repositories, Argon2id, JWT, MailKit, Hangfire
├── Pulse.UnitTests/       xUnit unit tests (≥ 80% coverage required)
└── Pulse.IntegrationTests/ xUnit + Testcontainers (real PostgreSQL)
```

---

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8)
- Docker (for local PostgreSQL via the dev compose file, or install PostgreSQL 16 directly)

---

## Environment setup

The `.env` file lives in `Pulse.Api/` alongside `appsettings.json`. It is loaded at startup by `EnvFileLoader` and is gitignored — never commit it.

```bash
cp Pulse.Api/.env.example Pulse.Api/.env
```

Minimum values to set in `.env` for local development:

| Variable | Description |
|---|---|
| `POSTGRES_PASSWORD` | Password for the local PostgreSQL instance |
| `JWT_SECRET_KEY` | Min 32-character signing key — generate with `openssl rand -base64 32` |
| `Mail__Password` | SMTP password for outbound email |

`appsettings.Development.json` is pre-configured to connect to `localhost:5432` with database `pulse_dev`. Adjust if your local Postgres differs.

> **Production RLS (two database roles).** Row-level security only protects data when the request path connects as a **non-superuser** role. In production set `DB_CONNECTION` to the limited runtime role (`pulse_rls_app`, `NOSUPERUSER`/`NOBYPASSRLS`) and `DB_MIGRATION_CONNECTION` to the table owner (migrations + Hangfire). Locally a single connection works, but RLS then filters nothing (a superuser or `BYPASSRLS` role is exempt), so the project's `docker-compose.yml` runs the same two-role split. Migrations and demo seeding identify as the `service` role so they see every row, and a server must run with `ASPNETCORE_ENVIRONMENT=Production`. Provisioning + verification: `docs/rls-runbook.md`.

---

## Running locally

**Option A — with Docker for Postgres only:**

```bash
# Start just the database
docker run -d --name pulse-pg -e POSTGRES_DB=pulse_dev -e POSTGRES_USER=pulse -e POSTGRES_PASSWORD=devpassword -p 5432:5432 postgres:16-alpine

# Run the API (must cd into Pulse.Api so EnvFileLoader finds .env)
cd Pulse.Api && dotnet run
```

See docs/getting-started.md for the full local setup guide.

The API will be available at `http://localhost:5000` (or the port in `launchSettings.json`). Swagger UI is at `http://localhost:5000/swagger`.

---

## Database migrations

Migrations are applied automatically on startup. To create a new migration after changing the domain:

```bash
dotnet ef migrations add <MigrationName> \
  --project src/backend/Pulse.Infrastructure \
  --startup-project src/backend/Pulse.Api \
  --output-dir Persistence/Migrations
```

Run this from the repo root. The `--startup-project` flag is required so the tooling can find the EF design package. EF uses `PulseDbContextFactory` in `Pulse.Infrastructure` at design time — it connects to `pulse_dev` on localhost and does not require the full app to start.

To apply manually:

```bash
dotnet ef database update \
  --project src/backend/Pulse.Infrastructure \
  --startup-project src/backend/Pulse.Api
```

**Rules:** never edit a committed migration — create a new one. Every migration must be backward-compatible for one version (expand-then-contract).

---

## Running tests

```bash
# Unit tests
dotnet test Pulse.UnitTests

# Integration tests (requires Docker — Testcontainers spins up PostgreSQL)
dotnet test Pulse.IntegrationTests

# All tests with coverage
dotnet test --collect:"XPlat Code Coverage" --results-directory ./coverage
```

Integration tests include the mandatory privacy gate (`Privacy/FeedbackPrivacyTests.cs`). A failure there blocks the build.

---

## Architecture quick reference

| Layer | Responsibility | Can call |
|---|---|---|
| `Pulse.Api` | HTTP, auth, validation, response shaping | Application (via MediatR) |
| `Pulse.Application` | Use cases, handlers, business orchestration | Domain, Infrastructure (via interfaces) |
| `Pulse.Domain` | Entities, invariants, `DomainException` | Nothing |
| `Pulse.Infrastructure` | EF Core, repositories, email, hashing | Domain |

See the [project architecture guide](../../handbook/project_architecture.md) for the full conventions.
