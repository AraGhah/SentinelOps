# ADR 0001: ASP.NET Core for the API

## Status

Accepted

## Context

The API needs JWT bearer auth against Cognito, a second custom
(HMAC-signed API key) auth scheme for alert ingestion, EF Core against
PostgreSQL, background hosted services (`DatabaseConnectionsMetricService`),
and to run as a long-lived ECS Fargate service behind an ALB — not a
request-scoped FaaS runtime, since it holds a warm Npgsql connection pool and
in-process rate limiting state across requests.

## Decision

ASP.NET Core Web API (C#, .NET 10), not Node/Express, Python/FastAPI, or Go.

Concretely, this gets used for things that would otherwise be hand-rolled:
`AddAuthentication().AddJwtBearer()` + a second
`AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>` for two
independent auth flows on the same app; `IExceptionHandler` for one place
that turns any unhandled exception into a `ProblemDetails` response
(`GlobalExceptionHandler`); `BackgroundService` for the connection-count
metrics poller; and `AddRateLimiter` for per-integration ingestion rate
limits — all first-party, not third-party packages bolted onto a thinner
framework.

## Consequences

- Same language (C#) as the Lambda workers and `SentinelOps.Events`/
  `SentinelOps.Workers.Shared` — event contracts and EF entities are shared
  types, not duplicated/hand-synced JSON schemas across two languages.
- Runs as an ECS Fargate service, not Lambda — see ADR 0007 for why the API
  specifically needs a long-lived process rather than a function.
- .NET's AOT/cold-start story matters far less here than it does for the
  workers (ADR 0008), since the API never cold-starts per request.
