# SentinelOps

[![CI](https://github.com/AraGhah/SentinelOps/actions/workflows/ci.yml/badge.svg)](https://github.com/AraGhah/SentinelOps/actions/workflows/ci.yml)
[![CD](https://github.com/AraGhah/SentinelOps/actions/workflows/cd.yml/badge.svg)](https://github.com/AraGhah/SentinelOps/actions/workflows/cd.yml)

SentinelOps is a cloud-native security operations platform for monitoring, detecting, and responding to threats across AWS environments in real time.

## Stack

| Layer          | Technology                                  |
| -------------- | -------------------------------------------- |
| Web            | Next.js, React, TypeScript                   |
| API            | ASP.NET Core Web API (C#)                    |
| Workers        | AWS Lambda (C#)                              |
| Database       | PostgreSQL + Entity Framework Core           |
| Cache          | Redis                                        |
| Infrastructure | AWS CDK (TypeScript)                         |

## Project structure

```
sentinelops/
├── apps/
│   ├── web/          # Next.js frontend
│   ├── api/           # ASP.NET Core Web API
│   └── workers/       # AWS Lambda functions (C#)
├── packages/          # Shared libraries
├── infrastructure/     # AWS CDK app
├── tests/              # Cross-app / integration tests
├── architecture/       # Architecture docs & diagrams
└── docs/                # Project documentation
```

## Getting started

### Prerequisites

- Node.js 20+
- .NET SDK 8+
- Docker + Docker Compose

### Local development

1. Copy the environment template:

   ```bash
   cp .env.example .env
   ```

2. Start the full stack:

   ```bash
   docker compose up --build
   ```

   This runs Postgres, Redis, the API (`http://localhost:5000`), and the web app (`http://localhost:3000`).

## Deployment

CI/CD runs via GitHub Actions (`.github/workflows/ci.yml`, `cd.yml`). Every push builds, tests, and `cdk synth`s all three environments (dev/staging/production). Pushes to `main` additionally build and push Docker images, deploy to staging, run e2e smoke tests, then deploy to production behind a required-reviewer approval gate. See [docs/deployment/rollback.md](docs/deployment/rollback.md) for rollback procedures and the one-time AWS/GitHub setup this pipeline needs before it can run.

## Cost

Every environment has an AWS Budget with 80%-actual/100%-forecasted alerts (`ObservabilityStack`), cost-allocation tags on every resource, S3 lifecycle rules, and environment-sized compute (see `infrastructure/lib/config/environments.ts`). See [docs/costs.md](docs/costs.md) for the estimate behind each environment's budget limit, and [docs/deployment/cleanup.md](docs/deployment/cleanup.md) for pausing dev, deleting stale snapshots, and the full `cdk destroy` procedure.

## Documentation

- [Architecture Decision Records](docs/adr/README.md) — why ASP.NET Core, PostgreSQL, EventBridge, SQS, Step Functions, DynamoDB, ECS Fargate, and Lambda were each chosen for the role they play.
- [Threat model](docs/security/threat-model.md) and [security assumptions](docs/security/security-assumptions.md).
- [Rollback procedures](docs/deployment/rollback.md) and [cleanup / cost-control procedures](docs/deployment/cleanup.md).
- [Cost estimate](docs/costs.md).

## License

MIT — see [LICENSE](LICENSE).
