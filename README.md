# NexDesk API

[![CI](https://github.com/mahmoudadel810/nexdesk-api/actions/workflows/ci.yml/badge.svg)](https://github.com/mahmoudadel810/nexdesk-api/actions/workflows/ci.yml)

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

Set the password in `appsettings.Development.json` to match `POSTGRES_PASSWORD` in `.env`.

## Tests

The integration tests start a throwaway PostgreSQL container, so Docker must be running.

```bash
dotnet test tests/NexDesk.UnitTests
dotnet test tests/NexDesk.IntegrationTests
dotnet format --verify-no-changes
```
