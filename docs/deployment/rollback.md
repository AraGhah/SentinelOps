# Rollback Procedures

Three scenarios, from "the deploy itself failed" to "everything looks fine
in CloudFormation but the app is broken."

## 1. A `cdk deploy` fails mid-rollout

CloudFormation rolls back automatically by default — `cdk deploy` (run by
`.github/workflows/cd.yml`) surfaces the failure and the stack returns to
its last good state. Nothing further to do in the common case.

If a stack gets stuck in `UPDATE_ROLLBACK_FAILED` (CloudFormation couldn't
even complete the automatic rollback — usually because a resource it's
trying to roll back to no longer matches reality):

```bash
aws cloudformation continue-update-rollback --stack-name <stack-name>
```

Re-run the failed CD workflow (or `cdk deploy` locally) once the stack is
back to `UPDATE_ROLLBACK_COMPLETE`.

## 2. A release deployed cleanly but misbehaves

Redeploy the previous known-good image tag (every image is tagged with the
git SHA it was built from — see `cd.yml`'s `build-and-push` job):

```bash
cd infrastructure
npx cdk deploy \
  -c env=<dev|staging|production> \
  -c apiImageTag=<previous-good-sha> \
  -c frontendImageTag=<previous-good-sha> \
  --all
```

Find `<previous-good-sha>` from the Actions tab (the last CD run before the
bad one) or `git log --oneline main`.

## 3. Emergency single-service rollback, without a full `cdk deploy`

Fastest path when only one service (API or frontend) is broken and you don't
want to wait for a full stack update: point the ECS service directly at the
previous task definition revision.

```bash
# List revisions to find the previous one:
aws ecs list-task-definitions --family-prefix sentinelops-api --sort DESC

# Point the service at it:
aws ecs update-service \
  --cluster sentinelops-cluster \
  --service sentinelops-api \
  --task-definition <previous-revision-arn> \
  --force-new-deployment
```

Same pattern for the frontend: `--family-prefix sentinelops-frontend
--cluster sentinelops-frontend-cluster --service sentinelops-frontend`.

This is a stopgap — the CDK stack's task definition still points at the
newer (bad) image tag, so the next `cdk deploy` will undo this rollback
unless you also deploy with the older `apiImageTag`/`frontendImageTag`
context values (scenario 2, above).

## One-time setup this repo needs before any of the above works

None of `deploy-staging`/`deploy-production` in `.github/workflows/cd.yml`
can run yet — they need, in order:

1. `cdk bootstrap` run once against the target AWS account/region.
2. `infrastructure/lib/stacks/cicd-stack.ts` (`SentinelOpsCiCd`) deployed
   once, to create the GitHub OIDC provider and deploy role.
3. That stack's `DeployRoleArn` output added as the `AWS_DEPLOY_ROLE_ARN`
   secret in this repo's Settings > Secrets and variables > Actions.
4. A `production` GitHub Environment created in Settings > Environments,
   with required reviewers configured — this is what makes
   `deploy-production` actually pause for approval; the workflow file alone
   doesn't enforce it.
5. A real Route53 hosted zone + domain, with `HostedZoneId`/`HostedZoneName`/
   `*DomainName` passed as `cdk deploy` context overrides (today's defaults
   are placeholders — see `docs/security/security-assumptions.md`).
