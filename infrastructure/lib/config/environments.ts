import * as cdk from 'aws-cdk-lib/core';
import * as logs from 'aws-cdk-lib/aws-logs';

export type EnvironmentName = 'dev' | 'staging' | 'production';

export interface EnvironmentConfig {
  envName: EnvironmentName;
  // CloudFront's ACM cert must be in us-east-1, so the whole app is pinned to that
  // region to avoid crossRegionReferences plumbing (see FrontendStack).
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
  // Governs Aurora, S3, and DynamoDB only. Cognito's UserPool is always RETAIN
  // regardless of environment — losing it invalidates every existing account.
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
  // Monthly AWS Budget limit in USD (see ObservabilityStack), a rough cap not a precise
  // cost projection. See docs/costs.md.
  monthlyBudgetUsd: number;
  // Exact browser origins allowed to call the API / upload to S3 (ApiStack CORS +
  // StorageStack attachments bucket CORS). Must be exact origins, never a wildcard.
  // If apps/web is hosted elsewhere (e.g. Vercel), put that deployed origin here.
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
    // apps/web's real staging origin (Vercel, see cd.yml's VERCEL_STAGING_URL).
    corsAllowedOrigins: ['https://sentinel-ops-ashen-beta.vercel.app'],
  },
  production: {
    envName: 'production',
    env: { region: 'us-east-1' },
    // One NAT gateway per AZ so an AZ outage doesn't take down outbound connectivity
    // for tasks in the other AZ.
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
    // Placeholder. Set to apps/web's real production origin before deploying.
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
