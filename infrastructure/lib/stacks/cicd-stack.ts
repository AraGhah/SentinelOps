import * as cdk from 'aws-cdk-lib/core';
import * as iam from 'aws-cdk-lib/aws-iam';
import { Construct } from 'constructs';

export interface CiCdStackProps extends cdk.StackProps {
  // e.g. "AraGhah/SentinelOps" — scopes which GitHub repo (any branch/PR/tag)
  // can assume the deploy role. Deliberately not per-branch: GitHub
  // Environments' required-reviewers protection (configured in repo
  // settings, not here) is what actually gates production, not this trust
  // condition.
  githubRepo: string;
}

// Deployed once per account/region, independent of dev/staging/production —
// unlike the other 8 stacks, this isn't parameterized by EnvironmentConfig
// and doesn't get a Dev/Staging/Production suffix.
export class CiCdStack extends cdk.Stack {
  constructor(scope: Construct, id: string, props: CiCdStackProps) {
    super(scope, id, props);

    // GitHub's own OIDC issuer — one provider per AWS account, not per repo.
    // If this AWS account already has this provider from another project,
    // deploying this stack will fail with "already exists"; import the
    // existing one instead in that case (IOidcProvider.fromOidcProviderArn).
    const githubOidcProvider = new iam.OidcProviderNative(this, 'GitHubOidcProvider', {
      url: 'https://token.actions.githubusercontent.com',
      clientIds: ['sts.amazonaws.com'],
    });

    // Trusts workflow runs from any ref/PR/environment in this one repo —
    // scoped no further than that, since IAM conditions aren't where the
    // production approval gate lives (GitHub Environments' required
    // reviewers is). Least-privilege is enforced instead via what this role
    // is *permitted to do* below: assume the CDK bootstrap roles (themselves
    // scoped to this account's CDK-managed resources) and push to exactly
    // two named ECR repos — nothing else.
    const deployRole = new iam.Role(this, 'GitHubActionsDeployRole', {
      roleName: 'sentinelops-github-actions-deploy',
      assumedBy: new iam.OpenIdConnectPrincipal(githubOidcProvider, {
        StringLike: { 'token.actions.githubusercontent.com:sub': `repo:${props.githubRepo}:*` },
        StringEquals: { 'token.actions.githubusercontent.com:aud': 'sts.amazonaws.com' },
      }),
      description:
        'Assumed by SentinelOps GitHub Actions workflows via OIDC - no long-lived AWS credentials stored in the repo.',
      maxSessionDuration: cdk.Duration.hours(1),
    });

    // `cdk deploy`/`cdk synth` never act as this role directly — they assume
    // CDK's own bootstrap roles (deploy-role, file-publishing-role,
    // image-publishing-role, lookup-role), which is what actually holds
    // permission to touch CloudFormation/S3/ECR-for-assets/etc. This role's
    // only job is to be allowed to assume those, scoped to this account.
    deployRole.addToPolicy(
      new iam.PolicyStatement({
        actions: ['sts:AssumeRole'],
        resources: [`arn:aws:iam::${this.account}:role/cdk-*`],
      }),
    );

    // apps/api and apps/web images are pushed directly by the CD workflow
    // (not through CDK's own asset-publishing role), since ApiStack/
    // FrontendStack reference these by fixed repository name rather than as
    // CDK-managed docker image assets.
    deployRole.addToPolicy(
      new iam.PolicyStatement({
        actions: ['ecr:GetAuthorizationToken'],
        resources: ['*'], // GetAuthorizationToken is account-scoped, not resource-scoped — AWS requires resources: ['*'] for this one action.
      }),
    );
    deployRole.addToPolicy(
      new iam.PolicyStatement({
        actions: [
          'ecr:BatchCheckLayerAvailability',
          'ecr:InitiateLayerUpload',
          'ecr:UploadLayerPart',
          'ecr:CompleteLayerUpload',
          'ecr:PutImage',
        ],
        resources: [
          `arn:aws:ecr:${this.region}:${this.account}:repository/sentinelops-api`,
          `arn:aws:ecr:${this.region}:${this.account}:repository/sentinelops-frontend`,
        ],
      }),
    );

    new cdk.CfnOutput(this, 'DeployRoleArn', {
      value: deployRole.roleArn,
      description:
        'Set as the AWS_DEPLOY_ROLE_ARN secret in the GitHub repo (Settings > Secrets and variables > Actions).',
    });
  }
}
