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
characters (`openssl rand -base64 48`) and `INBOUND_SECRET` with one of at least 16. The API applies its database migrations on startup.

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

## Customer portal and inbound email

Customers use the help centre of a workspace at `/portal/{slug}` in the web client. They sign up
with their own account (separate from staff, unique per workspace), follow their requests and
rate them once solved. Portal tokens use a different audience than staff tokens, so neither opens
the other's endpoints.

Email-to-ticket is simulated with a webhook a mail gateway would call:

```bash
scripts/send-inbound-email.sh acme casey@customer.example "Cannot sign in" "I get an error." "Casey"
scripts/send-inbound-email.sh acme casey@customer.example "Re: Cannot sign in [#12]" "It works now."
```

The first call opens a ticket. The second adds a reply to ticket 12 because the subject carries
its reference and the sender is its requester; otherwise it would open a new ticket. The webhook
is `POST /api/v1/inbound/email/{slug}` and requires the `X-Inbound-Secret` header to match
`INBOUND_SECRET` from `.env`.

## Public API, webhooks and the audit log

Workspaces on a plan with API access (Business) can issue API keys under Developers. A key is shown
once, stored only as a SHA-256 hash, and holds the `tickets:read` and/or `tickets:write` scope.
Integrations call the public API with it:

```bash
curl -H "X-Api-Key: ndk_..." http://localhost:8080/api/v1/public/tickets?search=refund
curl -X POST -H "X-Api-Key: ndk_..." -H "Content-Type: application/json" \
  -d '{"subject":"Order 1001 arrived damaged","description":"The box was crushed.","requesterEmail":"casey@customer.example"}' \
  http://localhost:8080/api/v1/public/tickets
```

Each key gets the plan's requests-per-minute allowance. Tickets it opens use the API channel, and
the key's name shows as the actor in the ticket timeline and the audit log.

Webhook endpoints receive `ticket.created`, `ticket.updated`, `ticket.assigned`, `ticket.replied`
and `ticket.solved` as JSON POSTs. Every request is signed:

```
X-NexDesk-Event: ticket.created
X-NexDesk-Delivery: <delivery id>
X-NexDesk-Timestamp: 1700000000
X-NexDesk-Signature: t=1700000000,v1=<hex HMAC-SHA256 of "1700000000.<raw body>" with the endpoint secret>
```

A receiver recomputes the HMAC over the timestamp, a dot and the raw body, compares it in constant
time, and rejects old timestamps to stop replays. A delivery that fails (no 2xx answer within 10
seconds) is retried by Hangfire after 1 minute, 5 minutes, 30 minutes, 2 hours and 6 hours, then
marked failed. Each attempt shows in the delivery log, and any delivery can be resent. Webhooks
refuse private, loopback and link-local addresses so a workspace cannot reach internal services;
set `WEBHOOKS_ALLOW_PRIVATE_NETWORKS=true` in `.env` to try them against a receiver on your machine.

The audit log records every create, change and delete of workspace records: who did it (a person,
an API key or the system), each field's value before and after, when, and from which address.
Secrets such as password and key hashes are never copied into it.

## Tests

The integration tests start a throwaway PostgreSQL container, so Docker must be running.

```bash
dotnet test tests/NexDesk.UnitTests
dotnet test tests/NexDesk.IntegrationTests
dotnet format --verify-no-changes
```

`scripts/verify.sh` runs the whole set (build with warnings as errors, both test projects and the
format check) and stops at the first failure. Every branch passes it before it is merged.
