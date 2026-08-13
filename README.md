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

CI/CD runs via GitHub Actions (`.github/workflows/ci.yml`, `cd.yml`) — every push builds, tests, and `cdk synth`s all three environments (dev/staging/production); pushes to `main` additionally build and push Docker images, deploy to staging, run e2e smoke tests, then deploy to production behind a required-reviewer approval gate. See [docs/deployment/rollback.md](docs/deployment/rollback.md) for rollback procedures and the one-time AWS/GitHub setup this pipeline needs before it can run.

## License

MIT — see [LICENSE](LICENSE).
