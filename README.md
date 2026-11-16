# NexDesk API

Backend for **NexDesk**, a multi-tenant help desk and SLA management SaaS. The web client lives in
[nexdesk-web](https://github.com/mahmoudadel810/nexdesk-web).

## Tech stack

| Area | Choice |
|------|--------|
| Runtime | .NET 10 (LTS), ASP.NET Core minimal APIs |
| Database | PostgreSQL 18 via EF Core 10 + Npgsql |
| Logging / tracing | Serilog with correlation IDs, OpenTelemetry |
| API docs | OpenAPI document + Scalar UI |
| Tests | xUnit, Shouldly, Testcontainers (real PostgreSQL) |

## Solution layout

```
src/
  NexDesk.Domain/          entities, value objects, enums, domain events
  NexDesk.Application/     use cases, DTOs, validators, interfaces
  NexDesk.Infrastructure/  EF Core, migrations, email, storage, jobs
  NexDesk.Api/             endpoints, auth, middleware, DI wiring
tests/
  NexDesk.UnitTests/
  NexDesk.IntegrationTests/
```

## Quick start

Prerequisites: Docker, and the .NET 10 SDK if you want to run outside containers.

```bash
cp .env.example .env
docker compose up --build
```

Before the first start, replace `JWT_SIGNING_KEY` in `.env` with a random value of at least 32
characters (`openssl rand -base64 48`). The API applies its database migrations on startup.

| Service | URL |
|---------|-----|
| API | http://localhost:8080 |
| API reference (Scalar) | http://localhost:8080/docs |
| OpenAPI document | http://localhost:8080/openapi/v1.json |
| Liveness / readiness | http://localhost:8080/health/live, http://localhost:8080/health/ready |
| Mailpit inbox | http://localhost:8025 |

### Running the API from source

```bash
docker compose up -d postgres mailpit
cp src/NexDesk.Api/appsettings.Development.example.json src/NexDesk.Api/appsettings.Development.json
dotnet run --project src/NexDesk.Api
```

Set the password in `appsettings.Development.json` to match `POSTGRES_PASSWORD` in `.env`, and
give `Jwt:SigningKey` a random value of at least 32 characters.

## Authentication

Staff sign in with email and password. The API answers with a 15-minute JWT access token and sets
a rotating refresh token in an `HttpOnly`, `SameSite=Strict` cookie scoped to `/api/v1/auth`.
Refresh tokens are stored hashed; presenting one that was already rotated revokes its whole
family. New workspaces must confirm their email address — in local development the message lands
in Mailpit at http://localhost:8025.

## Tests

The integration tests start a throwaway PostgreSQL container, so Docker must be running.

```bash
dotnet test tests/NexDesk.UnitTests
dotnet test tests/NexDesk.IntegrationTests
dotnet format --verify-no-changes
```
