# SentinelOps

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

## License

MIT — see [LICENSE](LICENSE).
