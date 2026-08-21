# Cleanup & Cost-Control Procedures

Complements `docs/costs.md` (what things cost) with what to actually run.

## Pausing dev without destroying it

```bash
cd infrastructure/scripts
./stop-dev-resources.sh   # scales API + frontend ECS services to 0 tasks
./start-dev-resources.sh  # scales them back up
```

Aurora Serverless v2 already scales its ACUs down on its own between these —
nothing to do there. NAT Gateway, ALB, and the Aurora minimum-ACU floor keep
billing even while scaled to zero tasks; only a full `cdk destroy` (below)
stops those.

## Deleting stale manual DB snapshots

```bash
cd infrastructure/scripts
./delete-old-snapshots.sh dev          # manual snapshots older than 30 days (default)
./delete-old-snapshots.sh production 90
```

Only touches manual snapshots (something a person took before a risky
change). Automated backups are governed by Aurora's own backup retention
setting on the cluster, not by this script.

## Full teardown: `cdk destroy`

Full environment teardown, in dependency order — reverse of
`bin/infrastructure.ts`'s creation order, since a stack can't be destroyed
while another stack still references its exports:

```bash
cd infrastructure
npx cdk destroy \
  SentinelOpsFrontendDev \
  SentinelOpsApiDev \
  SentinelOpsEventProcessingDev \
  SentinelOpsDatabaseDev \
  SentinelOpsObservabilityDev \
  SentinelOpsStorageDev \
  SentinelOpsAuthenticationDev \
  SentinelOpsNetworkDev \
  -c env=dev
```

(Swap the `Dev` suffix and `-c env=dev` for `Staging`/`staging` or
`Production`/`production`. `SentinelOpsCiCd` is account-wide, not
per-environment — only destroy it if no environment in the account needs the
GitHub OIDC deploy role anymore.)

`cdk destroy --all` also works and resolves the same order automatically —
the explicit list above exists mainly as documentation of what actually
depends on what.

### What survives destroy, by design

`removalPolicy.dataBearing` (`environments.ts`) is `RETAIN` for
staging/production, `DESTROY` for dev — so a staging/production `cdk destroy`
leaves behind (and keeps billing for, until manually deleted):

- The Aurora cluster's final snapshot and the `SentinelOpsDataKey` KMS key
  (`RETAIN` unconditionally, every environment — losing the CMK would make
  every KMS-encrypted resource's data unrecoverable, so this is intentional
  even in dev).
- The attachments/reports S3 buckets and their contents.
- The CloudTrail bucket and the Cognito User Pool (every existing account
  becomes unrecoverable if the pool is deleted — `RETAIN` regardless of
  environment).

Delete these manually only after confirming nothing still needs the data:

```bash
aws kms schedule-key-deletion --key-id <key-id> --pending-window-in-days 7
aws s3 rb s3://<bucket-name> --force
aws cognito-idp delete-user-pool --user-pool-id <pool-id>
```

### Before destroying production

1. Confirm the `MonthlyBudget` alarm topic (`ObservabilityStack`) isn't the
   reason you're here — check `docs/costs.md`'s "keeping this estimate
   honest" section first; destroying prod over a stale/misconfigured budget
   is more disruptive than fixing the budget.
2. Take a final manual DB snapshot if `RETAIN`'s automatic final snapshot
   isn't enough assurance: `aws rds create-db-cluster-snapshot ...`.
3. Confirm no other stack/environment depends on the shared
   `SentinelOpsCiCd` deploy role before touching that one.
