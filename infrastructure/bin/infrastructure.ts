import * as cdk from 'aws-cdk-lib/core';
import { resolveEnvironment } from '../lib/config/environments';
import { NetworkStack } from '../lib/stacks/network-stack';
import { AuthenticationStack } from '../lib/stacks/authentication-stack';
import { StorageStack } from '../lib/stacks/storage-stack';
import { ObservabilityStack } from '../lib/stacks/observability-stack';
import { DatabaseStack } from '../lib/stacks/database-stack';
import { EventProcessingStack } from '../lib/stacks/event-processing-stack';
import { ApiStack } from '../lib/stacks/api-stack';
import { FrontendStack } from '../lib/stacks/frontend-stack';
import { CiCdStack } from '../lib/stacks/cicd-stack';

// Entry point for `cdk synth` / `cdk deploy` (see cdk.json). One app, one environment
// per run, chosen with `-c env=dev|staging|production`.
//
// Context switches (all optional):
//   env                  dev (default) | staging | production
//   deployFrontend       "false" skips FrontendStack (apps/web is hosted on Vercel)
//   deployCustomDomain   "false" skips the Route53/ACM custom domain for the API and
//                        uses API Gateway's own HTTPS endpoint instead
//   apiImageTag          ECR tag for the API image (read by ApiStack)
//   frontendImageTag     ECR tag for the frontend image (read by FrontendStack)
//   githubRepo           "owner/repo" trusted by the GitHub OIDC deploy role
//
// Stack creation order below is the reverse of the teardown order documented in
// docs/deployment/cleanup.md. CDK derives the real dependencies from the cross-stack
// references, so the order here is for readability, not correctness.

// Context passed as `-c key=false` arrives as the string "false", while a value set in
// cdk.json arrives as a boolean.
function flag(app: cdk.App, key: string, defaultValue: boolean): boolean {
  const value = app.node.tryGetContext(key);
  if (value === undefined || value === null) return defaultValue;
  return String(value).toLowerCase() !== 'false';
}

const app = new cdk.App();
const config = resolveEnvironment(app);

const deployFrontend = flag(app, 'deployFrontend', true);
const deployCustomDomain = flag(app, 'deployCustomDomain', true);
const githubRepo = String(app.node.tryGetContext('githubRepo') ?? 'AraGhah/SentinelOps');

// staging and production still ship a placeholder, empty hosted zone id (no real domain
// exists for this project yet). A deploy that asks for a custom domain or the CloudFront
// frontend would only fail much later inside CloudFormation, so fail here instead. dev is
// exempt so `cdk synth -c env=dev` works with no AWS account (it is what CI synthesizes).
if (
  config.envName !== 'dev' &&
  (deployCustomDomain || deployFrontend) &&
  config.domains.hostedZoneIdDefault === ''
) {
  throw new Error(
    `"${config.envName}" has no hostedZoneIdDefault in lib/config/environments.ts, so a custom ` +
      `domain or the frontend stack cannot be deployed yet. Set a real Route53 hosted zone, or ` +
      `pass -c deployCustomDomain=false -c deployFrontend=false.`,
  );
}

// One shared env for every stack. It pins the whole app to a single region, which lets
// FrontendStack create its CloudFront certificate (us-east-1 only) without
// crossRegionReferences. No account is set, so synth needs no AWS credentials.
//
// The Environment tag is what Cost Explorer filters on (see docs/costs.md). It is set as a
// stack tag, which CloudFormation applies to every taggable resource at deploy time,
// rather than with Tags.of(), which would write a Tags property into each resource
// (including ones, like ALB listener rules, that the template validator rejects).
const env = config.env;
const stackProps = { env, tags: { Environment: config.envName } };

// e.g. SentinelOpsApiStaging. SentinelOpsCiCd is account-wide and has no suffix.
const suffix = config.envName.charAt(0).toUpperCase() + config.envName.slice(1);
const id = (name: string) => `SentinelOps${name}${suffix}`;

const network = new NetworkStack(app, id('Network'), { ...stackProps, config });

const authentication = new AuthenticationStack(app, id('Authentication'), {
  ...stackProps,
  config,
});

const storage = new StorageStack(app, id('Storage'), { ...stackProps, config });

const observability = new ObservabilityStack(app, id('Observability'), {
  ...stackProps,
  config,
  dataKey: storage.dataKey,
});

const database = new DatabaseStack(app, id('Database'), {
  ...stackProps,
  config,
  vpc: network.vpc,
  ecsSecurityGroup: network.ecsSecurityGroup,
  dataKey: storage.dataKey,
});

const eventProcessing = new EventProcessingStack(app, id('EventProcessing'), {
  ...stackProps,
  config,
  vpc: network.vpc,
  ecsSecurityGroup: network.ecsSecurityGroup,
  dbSecret: database.secret,
  fingerprintTable: storage.fingerprintTable,
});

const api = new ApiStack(app, id('Api'), {
  ...stackProps,
  config,
  vpc: network.vpc,
  albSecurityGroup: network.albSecurityGroup,
  vpcLinkSecurityGroup: network.vpcLinkSecurityGroup,
  ecsSecurityGroup: network.ecsSecurityGroup,
  dbSecret: database.secret,
  userPool: authentication.userPool,
  userPoolClient: authentication.userPoolClient,
  attachmentsBucket: storage.attachmentsBucket,
  reportsBucket: storage.reportsBucket,
  connectionsTable: storage.connectionsTable,
  eventBus: eventProcessing.eventBus,
  dataKey: storage.dataKey,
  apiLogGroup: observability.apiLogGroup,
  deployCustomDomain,
});

if (deployFrontend) {
  new FrontendStack(app, id('Frontend'), {
    ...stackProps,
    config,
    vpc: network.vpc,
    apiUrl: api.apiUrl,
  });
}

// Deployed once per account (GitHub OIDC provider + deploy role), independent of which
// environment is being synthesized. It belongs to no single environment, so it gets a
// fixed tag value instead of flipping between staging and production on every deploy.
new CiCdStack(app, 'SentinelOpsCiCd', { env, tags: { Environment: 'shared' }, githubRepo });
