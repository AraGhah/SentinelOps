import * as cdk from 'aws-cdk-lib/core';
import * as logs from 'aws-cdk-lib/aws-logs';

export type EnvironmentName = 'dev' | 'staging' | 'production';

export interface EnvironmentConfig {
  envName: EnvironmentName;
  // Every stack in bin/infrastructure.ts shares this one `env` object
  // deliberately — the FrontendStack's CloudFront ACM certificate must live
  // in us-east-1, and pinning the whole app to us-east-1 lets that
  // certificate be created in the same stack/region as everything else with
  // no crossRegionReferences plumbing. If a future environment needs a
  // different primary region, the CloudFront cert has to move to its own
  // us-east-1-pinned stack instead — see FrontendStack's header comment.
  env: { region: string };
  natGateways: number;
  aurora: { minCapacityAcu: number; maxCapacityAcu: number };
  ecs: {
    apiDesiredCount: number;
    frontendDesiredCount: number;
    // apiDesiredCount is also the autoscaling floor; ApiStack scales up to
    // apiMaxCapacity on CPU/request-count pressure.
    apiMaxCapacity: number;
  };
  logRetention: logs.RetentionDays;
  // Governs Aurora, S3, and DynamoDB removal policies — NOT Cognito (its
  // UserPool is always RETAIN regardless of environment, since losing it
  // invalidates every existing account, which is disruptive even in dev).
  removalPolicy: {
    dataBearing: cdk.RemovalPolicy;
    compute: cdk.RemovalPolicy;
  };
  domains: {
    apiDomainNameDefault: string;
    frontendDomainNameDefault: string;
    hostedZoneIdDefault: string;
    hostedZoneNameDefault: string;
  };
  notificationDomainNameDefault: string;
  // Monthly AWS Budget limit in USD (see ObservabilityStack) — a rough cap
  // per environment, not a precise cost projection. See docs/costs.md for
  // the estimate this is based on.
  monthlyBudgetUsd: number;
  // Exact browser origins allowed to call the API and upload attachments
  // directly to S3 (ApiStack's CORS policy + StorageStack's attachments
  // bucket CORS rules — see both for why this must be an exact origin list,
  // never a wildcard). This is apps/web's *deployed* origin — if apps/web is
  // hosted somewhere other than this app's own FrontendStack (e.g. Vercel),
  // put that origin here instead/as well: a Vercel production domain
  // (`https://your-app.vercel.app` or a custom domain) and, if preview
  // deployments need to hit this environment's API too, each preview origin
  // (Vercel preview URLs aren't a fixed pattern you can wildcard against
  // AllowCredentials CORS, so add them individually as needed, or point
  // previews at the dev environment's API instead).
  corsAllowedOrigins: string[];
}

export const environments: Record<EnvironmentName, EnvironmentConfig> = {
  dev: {
    envName: 'dev',
    env: { region: 'us-east-1' },
    natGateways: 1,
    aurora: { minCapacityAcu: 0.5, maxCapacityAcu: 2 },
    ecs: { apiDesiredCount: 1, frontendDesiredCount: 1, apiMaxCapacity: 3 },
    logRetention: logs.RetentionDays.TWO_WEEKS,
    removalPolicy: { dataBearing: cdk.RemovalPolicy.DESTROY, compute: cdk.RemovalPolicy.DESTROY },
    domains: {
      apiDomainNameDefault: 'api-dev.sentinelops.example',
      frontendDomainNameDefault: 'app-dev.sentinelops.example',
      hostedZoneIdDefault: '',
      hostedZoneNameDefault: 'sentinelops.example',
    },
    notificationDomainNameDefault: 'alerts-dev.sentinelops.example',
    monthlyBudgetUsd: 50,
    corsAllowedOrigins: ['http://localhost:3000'],
  },
  staging: {
    envName: 'staging',
    env: { region: 'us-east-1' },
    natGateways: 1,
    aurora: { minCapacityAcu: 0.5, maxCapacityAcu: 4 },
    ecs: { apiDesiredCount: 2, frontendDesiredCount: 2, apiMaxCapacity: 6 },
    logRetention: logs.RetentionDays.ONE_MONTH,
    removalPolicy: { dataBearing: cdk.RemovalPolicy.RETAIN, compute: cdk.RemovalPolicy.DESTROY },
    domains: {
      apiDomainNameDefault: 'api-staging.sentinelops.example',
      frontendDomainNameDefault: 'app-staging.sentinelops.example',
      hostedZoneIdDefault: '',
      hostedZoneNameDefault: 'sentinelops.example',
    },
    notificationDomainNameDefault: 'alerts-staging.sentinelops.example',
    monthlyBudgetUsd: 150,
    // apps/web's real staging origin (Vercel — see cd.yml's
    // VERCEL_STAGING_URL). Keep this in sync if that alias ever changes.
    corsAllowedOrigins: ['https://sentinel-ops-ashen-beta.vercel.app'],
  },
  production: {
    envName: 'production',
    env: { region: 'us-east-1' },
    // Unlike dev/staging: one NAT gateway per AZ, so an AZ-level outage
    // doesn't take outbound connectivity down for every ECS task/Lambda in
    // the other AZ too.
    natGateways: 2,
    aurora: { minCapacityAcu: 1, maxCapacityAcu: 8 },
    ecs: { apiDesiredCount: 2, frontendDesiredCount: 2, apiMaxCapacity: 10 },
    logRetention: logs.RetentionDays.THREE_MONTHS,
    removalPolicy: { dataBearing: cdk.RemovalPolicy.RETAIN, compute: cdk.RemovalPolicy.DESTROY },
    domains: {
      apiDomainNameDefault: 'api.sentinelops.example',
      frontendDomainNameDefault: 'app.sentinelops.example',
      hostedZoneIdDefault: '',
      hostedZoneNameDefault: 'sentinelops.example',
    },
    notificationDomainNameDefault: 'alerts.sentinelops.example',
    monthlyBudgetUsd: 500,
    // Placeholder — see the staging entry's comment. Set to apps/web's real
    // production origin (e.g. your Vercel production domain/custom domain)
    // before deploying.
    corsAllowedOrigins: [],
  },
};

// Reads `-c env=dev|staging` (falls back to SENTINELOPS_ENV, then 'dev') so
// `cdk synth`/`cdk deploy` can target either environment from one app without
// AWS credentials or account access being required at synth time.
export function resolveEnvironment(app: cdk.App): EnvironmentConfig {
  const envName = (app.node.tryGetContext('env') ??
    process.env.SENTINELOPS_ENV ??
    'dev') as EnvironmentName;
  const config = environments[envName];
  if (!config) {
    throw new Error(
      `Unknown environment "${envName}" — expected one of: ${Object.keys(environments).join(', ')}`,
    );
  }
  return config;
}
