# AWS Cost Estimate

Rough monthly estimates, us-east-1, on-demand pricing as of writing. These are
**estimates for planning the budgets below, not a bill** — actual cost depends
on real traffic, AWS's current pricing, and free-tier eligibility. Every
number here assumes the resource sizes in
`infrastructure/lib/config/environments.ts`.

## Dev

Single AZ, minimum-sized everything, `stop-dev-resources.sh` run outside
working hours.

| Resource | Estimate |
| --- | --- |
| NAT Gateway (1) | ~$33/mo + data processing |
| Aurora Serverless v2 (0.5–2 ACU, mostly idle at the floor) | ~$45/mo |
| ECS Fargate — API (1 task, 0.5 vCPU/1GB) | ~$15/mo |
| ECS Fargate — frontend (1 task, 0.25 vCPU/0.5GB) | ~$7/mo |
| Application Load Balancer (x2 — API + frontend) | ~$32/mo |
| Lambda (10 workers, low volume) | <$1/mo |
| DynamoDB on-demand (fingerprint + connections tables) | <$1/mo |
| S3 (attachments, reports, CloudTrail, frontend origin) | <$2/mo |
| SQS + EventBridge | <$1/mo |
| Cognito (MAU-based, free under 50k MAU) | $0/mo |
| CloudWatch Logs + custom metrics + dashboard | ~$5/mo |
| Secrets Manager (1 secret) | ~$0.40/mo |
| KMS (1 CMK) | ~$1/mo |
| CloudFront (frontend CDN) | <$1/mo at low traffic |
| SES | <$1/mo at low volume |
| **Total** | **~$140–150/mo**, less with `stop-dev-resources.sh` |

Budget limit configured: **$50/mo** (`environments.ts` → `monthlyBudgetUsd`),
set below the always-on estimate above on the assumption dev is scaled down
outside active development. Raise it if that assumption doesn't hold for how
you're actually using this environment.

## Staging

Same shape as dev, roughly 2x Fargate/Aurora headroom (`ecs.apiDesiredCount:
2`, `aurora.maxCapacityAcu: 4`).

| Category | Estimate |
| --- | --- |
| Compute (ECS + Lambda) | ~$50/mo |
| Aurora Serverless v2 | ~$60/mo |
| Networking (NAT, ALB, CloudFront) | ~$70/mo |
| Storage + data services (S3, DynamoDB, Secrets Manager, KMS) | ~$10/mo |
| Observability (CloudWatch) | ~$8/mo |
| **Total** | **~$200/mo** |

Budget limit: **$150/mo**.

## Production

Two NAT gateways (AZ redundancy), `apiDesiredCount: 2` with autoscaling to
10, `aurora.maxCapacityAcu: 8`.

| Category | Estimate |
| --- | --- |
| Compute (ECS + Lambda, at baseline — autoscaling adds more under load) | ~$90/mo |
| Aurora Serverless v2 | ~$120/mo |
| Networking (2x NAT, 2x ALB, CloudFront) | ~$140/mo |
| Storage + data services | ~$20/mo |
| Observability (CloudTrail, CloudWatch, X-Ray) | ~$20/mo |
| **Total** | **~$390/mo** at baseline, more under sustained load |

Budget limit: **$500/mo** — headroom above baseline for autoscaling before
the 80%-actual/100%-forecasted alarms (see `ObservabilityStack`) fire.

## Biggest cost levers

1. **NAT Gateways** — the single largest fixed cost per environment. Dev and
   staging run one; production runs two for AZ redundancy. A NAT instance
   (not gateway) would be cheaper but loses the managed HA/throughput —
   not worth it below production traffic levels.
2. **Aurora Serverless v2's ACU floor** — `minCapacityAcu` never scales to
   zero (a v2 limitation), so it's a cost floor even fully idle. Dev's 0.5
   ACU floor is already the minimum Aurora Serverless v2 allows.
3. **Two ALBs per environment** (API + frontend) — a structural
   choice (`api-stack.ts`, `frontend-stack.ts`), not something this app
   currently avoids. Consolidating onto one ALB with path-based routing
   would save one ALB's fixed cost per environment if that becomes worth
   the added routing complexity.

## Keeping this estimate honest

- Re-check actual spend against these numbers via Cost Explorer, filtered by
  the `Environment` tag every stack carries (`bin/infrastructure.ts`).
- The `MonthlyBudget` in `ObservabilityStack` alerts at 80% actual / 100%
  forecasted spend against `monthlyBudgetUsd` — treat a repeated breach as a
  signal this doc's estimate (or the budget limit itself) is stale, not
  something to just raise the limit past without checking why.
