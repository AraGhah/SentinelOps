# Security Assumptions

Explicit tradeoffs made while implementing the security checklist and the
8-stack infrastructure split, so they're reviewed deliberately rather than
discovered by accident later.

- **ALB → ECS traffic is plaintext HTTP.** TLS terminates at each ALB
  (`SslPolicy.RECOMMENDED_TLS`); the hop from an ALB to its ECS task inside
  the VPC is HTTP, protected only by the security-group boundary. This is a
  common and generally accepted pattern given the traffic never leaves the
  VPC, but it means anyone who gains a foothold inside `PrivateApp` (a
  compromised ECS task, for instance) could observe unencrypted
  request/response bodies from other tasks' traffic.

- **The API's ALB is private; API Gateway (HTTP API) + a VpcLink is the
  public entry point instead.** WAF now attaches to the API Gateway stage,
  not the ALB. The frontend's ALB, by contrast, stays **public** — CloudFront
  requires a publicly reachable origin (short of the newer, less broadly
  supported VPC-origins feature) — but its security group only grants public
  ingress at the network layer; the ALB listener itself rejects (403) any
  request missing a shared-secret `X-Origin-Verify` header that only
  CloudFront is configured to send. Anyone who discovers the frontend ALB's
  DNS name directly still can't get past the listener rule without that
  header value.

- **Single NAT gateway.** `natGateways` (from `EnvironmentConfig`) is `1` for
  both dev and staging — ECS, the RDS rotation Lambda, and every VPC-attached
  worker Lambda all share one NAT gateway for outbound internet access (ECR
  pulls, Secrets Manager, S3, EventBridge, SES, Cognito). It's a single point
  of failure: if that NAT gateway's AZ has an outage, outbound connectivity
  from the other AZ's resources breaks too. Production would normally use one
  NAT gateway per AZ.

- **Aurora Serverless v2 runs a single writer instance, no reader.** Same
  cost/simplicity tradeoff the previous single-AZ RDS setup made — no
  automatic failover on an AZ-level outage. `serverlessV2MinCapacity`/
  `MaxCapacity` are configured per environment (dev: 0.5–2 ACUs, staging:
  0.5–4 ACUs).

- **The Aurora-generated Secrets Manager secret does NOT use the shared
  customer-managed KMS key** (`StorageStack.dataKey`) — deliberately, and for
  a structural reason, not an oversight. `Secret.grantRead()` always grants
  KMS decrypt via a `kms.ViaServicePrincipal` wrapper (Secrets Manager calls
  KMS on the caller's behalf), and that wrapper can't hold its own identity
  policy — so the grant always falls back to mutating the *key's* resource
  policy with the grantee's specific role ARN. With every worker Lambda
  across `EventProcessingStack` and `ApiStack` calling `dbSecret.grantRead()`,
  sharing `dataKey` for this secret would make `StorageStack` depend on
  those downstream stacks, which already depend on `StorageStack` (directly
  or via `DatabaseStack`) — a real circular CloudFormation dependency, not
  something `cdk deploy` can resolve. The secret is still encrypted at rest
  (Secrets Manager's own AWS-owned default key), just not with the account's
  customer-managed key. `dataKey` still covers RDS storage encryption, both
  S3 buckets, the ECR repository, and CloudTrail.

- **The RDS/rotation security groups live in `DatabaseStack`, not
  `NetworkStack`**, even though every other security group lives in
  `NetworkStack`. `secretsmanager.SecretRotation` internally mutates the
  target's (Aurora's) security group using a port token derived from the
  cluster's own endpoint attribute — doing that across a stack boundary
  creates the same class of circular dependency as the KMS issue above.
  Keeping the RDS/rotation security groups in the same stack as the cluster
  sidesteps it. The connection from `ecsSecurityGroup` (in `NetworkStack`) to
  Aurora is consequently asymmetric by design: egress out of
  `ecsSecurityGroup` is CIDR-scoped to the VPC's address range (not SG-ID-
  scoped to `DatabaseStack`'s RDS security group specifically), while ingress
  *into* the RDS security group is SG-ID-scoped to `ecsSecurityGroup`. See
  the comments in `network-stack.ts` and `database-stack.ts` for the full
  reasoning — this is the one place in the network layer that isn't a purely
  symmetric SG-to-SG reference.

- **Lambda workers and the ECS API task resolve the DB secret only at cold
  start / process start.** Each worker reads `DB_SECRET_ARN` once (see
  `DbConnectionStringResolver.FromEnvironment()` in
  `SentinelOps.Workers.Shared`); the ECS task reads `DB_SECRET_JSON` once at
  startup (`ApiDbConnectionStringResolver` in `apps/api/Common`). When the
  Aurora credential rotates (every 30 days, automatically), already-warm
  Lambda execution environments and running ECS tasks keep using the
  pre-rotation password until they're recycled/redeployed — rotation takes
  effect gradually, not instantly.

- **Content-Security-Policy is maximally strict** (`default-src 'none';
  frame-ancestors 'none'`) because `apps/api` is a pure JSON API and never
  renders HTML outside `Development`'s Swagger UI. If that changes, this
  policy needs to be relaxed deliberately, not silently broken by widening it
  without review.

- **The WAF rate-based rule (2000 requests / 5 minutes / IP) is a coarse
  edge backstop**, not a replacement for the app-level rate limiter already in
  `Program.cs` (5/min for auth, 100/10s per-JWT-sub for the general API,
  50/10s per-integration for ingestion). It's deliberately loose so it
  doesn't false-positive against a shared corporate NAT IP with many users
  behind it.

- **ECR image tags are `latest` in both task definitions** (API and
  frontend) — placeholders. A real deployment pipeline should push
  immutable, content-addressed tags (e.g. the git SHA) and update the task
  definitions per release, both for rollback safety and so
  `docker-scan.yml`'s Trivy scan result is traceable to the exact image
  running in an environment.

- **`apps/web` has no production Dockerfile yet.** `FrontendStack`'s ECS
  task definition references an image built from `apps/web/Dockerfile`,
  which currently runs `npm run dev` — fine for local `docker-compose`, not
  for the ECS deployment this stack describes. The CDK code synthesizes
  cleanly regardless (it never inspects image contents), but the frontend
  isn't truly deployable until `apps/web` gets the same multi-stage
  production-build treatment `apps/api/Dockerfile` already has.

- **Every environment shares one AWS region (`us-east-1`), by design.**
  `FrontendStack`'s CloudFront distribution needs its ACM certificate in
  us-east-1 — a hard CloudFront requirement — and pinning every stack in
  every environment to that one region lets the certificate be created in
  the same stack, with no `crossRegionReferences` plumbing. `bin/infrastructure.ts`
  builds one shared `env` object per environment specifically to keep this
  true; if a future environment needs a different primary region, the
  CloudFront certificate must move to its own us-east-1-pinned stack.

- **Route53 hosted zone and ACM certificates are provisioned against
  `CfnParameter` placeholders, not a real owned domain.** `HostedZoneId`,
  `HostedZoneName`, `ApiDomainName`, and `FrontendDomainName` all have
  environment-specific string defaults (e.g. `api-dev.sentinelops.example`)
  but no real hosted zone exists yet. The hosted zone is imported via
  `HostedZone.fromHostedZoneAttributes` (not `fromLookup`) specifically so
  `cdk synth` succeeds with zero AWS credentials — deploying for real
  requires overriding these parameters with a real hosted zone ID/domain at
  deploy time.

- **This infrastructure is written and synthesized (`cdk synth -c
  env=dev|staging` both succeed) but not deployed.** No `cdk deploy` or `cdk
  diff` has been run — this environment has no AWS credentials configured,
  and deploying either environment for real has cost and blast-radius
  implications that warrant a separate, explicit decision once real
  credentials and a real domain are available.
