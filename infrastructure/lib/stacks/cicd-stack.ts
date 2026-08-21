import * as cdk from 'aws-cdk-lib/core';
import * as iam from 'aws-cdk-lib/aws-iam';
import { Construct } from 'constructs';

export interface CiCdStackProps extends cdk.StackProps {
  // e.g. "AraGhah/SentinelOps" — scopes which GitHub repo can assume the deploy role.
  // Not per-branch: GitHub Environments' required-reviewers protection (repo settings,
  // not here) is what actually gates production.
  githubRepo: string;
}

// Deployed once per account/region, independent of dev/staging/production; doesn't
// get a Dev/Staging/Production suffix like the other stacks.
export class CiCdStack extends cdk.Stack {
  constructor(scope: Construct, id: string, props: CiCdStackProps) {
    super(scope, id, props);

    // GitHub's own OIDC issuer, one provider per AWS account, not per repo. If this
    // account already has it from another project, deploy fails with "already
    // exists"; import the existing one instead (IOidcProvider.fromOidcProviderArn).
    const githubOidcProvider = new iam.OidcProviderNative(this, 'GitHubOidcProvider', {
      url: 'https://token.actions.githubusercontent.com',
      clientIds: ['sts.amazonaws.com'],
    });

    // Trusts workflow runs from any ref/PR/environment in this one repo; the
    // production approval gate lives in GitHub Environments' required reviewers, not
    // here. Least-privilege is enforced by what this role can do below instead.
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

    // cdk deploy/synth never act as this role directly; they assume CDK's own
    // bootstrap roles, which hold the actual CloudFormation/S3/ECR permissions. This
    // role's only job is to be allowed to assume those, scoped to this account.
    deployRole.addToPolicy(
      new iam.PolicyStatement({
        actions: ['sts:AssumeRole'],
        resources: [`arn:aws:iam::${this.account}:role/cdk-*`],
      }),
    );

    // apps/api and apps/web images are pushed directly by the CD workflow, not through
    // CDK's asset-publishing role, since ApiStack/FrontendStack reference these by
    // fixed repository name rather than as CDK-managed docker image assets.
    deployRole.addToPolicy(
      new iam.PolicyStatement({
        actions: ['ecr:GetAuthorizationToken'],
        resources: ['*'], // GetAuthorizationToken is account-scoped; AWS requires resources: ['*'] for this action.
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
