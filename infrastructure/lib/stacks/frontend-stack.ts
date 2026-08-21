import * as cdk from 'aws-cdk-lib/core';
import * as ec2 from 'aws-cdk-lib/aws-ec2';
import * as ecr from 'aws-cdk-lib/aws-ecr';
import * as ecs from 'aws-cdk-lib/aws-ecs';
import * as elbv2 from 'aws-cdk-lib/aws-elasticloadbalancingv2';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as logs from 'aws-cdk-lib/aws-logs';
import * as cloudfront from 'aws-cdk-lib/aws-cloudfront';
import * as cloudfrontOrigins from 'aws-cdk-lib/aws-cloudfront-origins';
import * as certificatemanager from 'aws-cdk-lib/aws-certificatemanager';
import * as route53 from 'aws-cdk-lib/aws-route53';
import * as route53Targets from 'aws-cdk-lib/aws-route53-targets';
import * as secretsmanager from 'aws-cdk-lib/aws-secretsmanager';
import * as wafv2 from 'aws-cdk-lib/aws-wafv2';
import { Construct } from 'constructs';
import { EnvironmentConfig } from '../config/environments';

export interface FrontendStackProps extends cdk.StackProps {
  config: EnvironmentConfig;
  vpc: ec2.IVpc;
  apiUrl: string;
}

// NOTE: this stack's ACM certificate must be in us-east-1 (a hard CloudFront
// requirement). It's created in *this* stack, in *this* stack's own region,
// which only works because bin/infrastructure.ts pins every stack's region to
// us-east-1 via one shared `env` object (see environments.ts). If a future
// environment's primary region isn't us-east-1, this certificate needs to
// move to its own us-east-1-pinned stack with `crossRegionReferences: true`.
export class FrontendStack extends cdk.Stack {
  constructor(scope: Construct, id: string, props: FrontendStackProps) {
    super(scope, id, props);

    const { vpc } = props;

    // apps/web's Dockerfile currently runs `npm run dev` with no production
    // build (next.config.ts has no `output: 'export'` either) — this stack's
    // CDK code synthesizes fine (it never reads image contents) but isn't
    // truly deployable until apps/web gets a production multi-stage
    // Dockerfile, the same treatment apps/api/Dockerfile already got.
    const frontendRepository = new ecr.Repository(this, 'FrontendRepository', {
      repositoryName: 'sentinelops-frontend',
      imageScanOnPush: true,
      removalPolicy: props.config.removalPolicy.compute,
    });

    const frontendCluster = new ecs.Cluster(this, 'FrontendCluster', {
      vpc,
      clusterName: 'sentinelops-frontend-cluster',
      containerInsightsV2: ecs.ContainerInsights.ENABLED,
    });

    const frontendExecutionRole = new iam.Role(this, 'FrontendExecutionRole', {
      assumedBy: new iam.ServicePrincipal('ecs-tasks.amazonaws.com'),
    });
    frontendRepository.grantPull(frontendExecutionRole);

    const frontendTaskRole = new iam.Role(this, 'FrontendTaskRole', {
      assumedBy: new iam.ServicePrincipal('ecs-tasks.amazonaws.com'),
    });

    const frontendLogGroup = new logs.LogGroup(this, 'FrontendLogGroup', {
      logGroupName: '/sentinelops/frontend',
      retention: props.config.logRetention,
      removalPolicy: props.config.removalPolicy.compute,
    });

    // A shared-secret custom header CloudFront injects on every origin
    // request and the ALB listener rule below requires — this is what stops
    // the ALB (which must stay internet-reachable for CloudFront's standard
    // origin fetch, unlike the API's ALB behind a VpcLink) from being usable
    // by anyone who finds its DNS name directly.
    //
    // Previously a required CfnParameter with no default — since CD never
    // passed `--parameters OriginVerifySecret=...`, every deploy failed at
    // the CloudFormation level. This stack now owns the secret's lifecycle
    // end-to-end: CDK generates and stores it in Secrets Manager itself (same
    // `fromGeneratedSecret`-style pattern DatabaseStack uses for the RDS
    // credentials), so no manual parameter ever needs to be supplied.
    // `.secretValue.unsafeUnwrap()` is safe here specifically because both
    // consumers below (CloudFront's customHeaders map and the ALB listener
    // condition's string[]) only need a CDK token string — it resolves to a
    // `{{resolve:secretsmanager:...}}` dynamic reference in the synthesized
    // template, not a literal value, so the real secret never appears in the
    // CFN template or CDK output.
    const originVerifySecret = new secretsmanager.Secret(this, 'OriginVerifySecret', {
      secretName: `sentinelops/${props.config.envName}/origin-verify-secret`,
      description:
        'Shared secret CloudFront sends as X-Origin-Verify; the ALB rejects requests without it.',
      generateSecretString: { excludePunctuation: true, passwordLength: 32 },
    }).secretValue.unsafeUnwrap();

    const frontendTaskDefinition = new ecs.FargateTaskDefinition(this, 'FrontendTaskDefinition', {
      family: 'sentinelops-frontend',
      cpu: 256,
      memoryLimitMiB: 512,
      executionRole: frontendExecutionRole,
      taskRole: frontendTaskRole,
    });
    // Same context-parameter pattern as ApiStack's apiImageTag.
    const frontendImageTag = this.node.tryGetContext('frontendImageTag') ?? 'latest';
    frontendTaskDefinition.addContainer('FrontendContainer', {
      image: ecs.ContainerImage.fromEcrRepository(frontendRepository, frontendImageTag),
      logging: ecs.LogDrivers.awsLogs({ streamPrefix: 'frontend', logGroup: frontendLogGroup }),
      environment: {
        NEXT_PUBLIC_API_URL: props.apiUrl,
      },
      portMappings: [{ containerPort: 3000 }],
      stopTimeout: cdk.Duration.seconds(30),
    });

    const frontendAlbSecurityGroup = new ec2.SecurityGroup(this, 'FrontendAlbSecurityGroup', {
      vpc,
      description:
        'SentinelOps frontend ALB - public, but only CloudFront (via X-Origin-Verify) gets past the listener rule',
      allowAllOutbound: false,
    });
    const frontendEcsSecurityGroup = new ec2.SecurityGroup(this, 'FrontendEcsSecurityGroup', {
      vpc,
      description: 'SentinelOps frontend ECS tasks',
      allowAllOutbound: false,
    });
    frontendAlbSecurityGroup.addIngressRule(
      ec2.Peer.anyIpv4(),
      ec2.Port.tcp(443),
      'Public HTTPS (CloudFront and anyone else — rejected at the listener rule without the origin-verify header)',
    );
    frontendAlbSecurityGroup.addIngressRule(
      ec2.Peer.anyIpv4(),
      ec2.Port.tcp(80),
      'Public HTTP (redirected to HTTPS)',
    );
    frontendAlbSecurityGroup.connections.allowTo(
      frontendEcsSecurityGroup,
      ec2.Port.tcp(3000),
      'ALB to frontend containers',
    );
    frontendEcsSecurityGroup.addEgressRule(
      ec2.Peer.anyIpv4(),
      ec2.Port.tcp(443),
      'Outbound HTTPS (ECR pulls, etc.)',
    );

    const frontendService = new ecs.FargateService(this, 'FrontendService', {
      cluster: frontendCluster,
      taskDefinition: frontendTaskDefinition,
      desiredCount: props.config.ecs.frontendDesiredCount,
      securityGroups: [frontendEcsSecurityGroup],
      vpcSubnets: { subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS },
      assignPublicIp: false,
      minHealthyPercent: 100,
      circuitBreaker: { rollback: true },
      healthCheckGracePeriod: cdk.Duration.seconds(60),
    });

    const frontendAlb = new elbv2.ApplicationLoadBalancer(this, 'FrontendAlb', {
      loadBalancerName: 'sentinelops-frontend-alb',
      vpc,
      internetFacing: true,
      securityGroup: frontendAlbSecurityGroup,
      vpcSubnets: { subnetType: ec2.SubnetType.PUBLIC },
    });

    const frontendTargetGroup = new elbv2.ApplicationTargetGroup(this, 'FrontendTargetGroup', {
      vpc,
      port: 3000,
      protocol: elbv2.ApplicationProtocol.HTTP,
      targetType: elbv2.TargetType.IP,
      targets: [frontendService],
      healthCheck: { path: '/', healthyHttpCodes: '200-399' },
      deregistrationDelay: cdk.Duration.seconds(20),
    });

    // --- DNS + ACM (us-east-1, for CloudFront) --------------------------------
    const hostedZoneId = new cdk.CfnParameter(this, 'HostedZoneId', {
      type: 'String',
      default: props.config.domains.hostedZoneIdDefault,
      description: 'Route53 hosted zone ID that owns the API/frontend domains.',
    }).valueAsString;
    const hostedZoneName = new cdk.CfnParameter(this, 'HostedZoneName', {
      type: 'String',
      default: props.config.domains.hostedZoneNameDefault,
      description: 'Route53 hosted zone name (e.g. sentinelops.example).',
    }).valueAsString;
    const frontendDomainName = new cdk.CfnParameter(this, 'FrontendDomainName', {
      type: 'String',
      default: props.config.domains.frontendDomainNameDefault,
      description: 'Public domain name for the web app (e.g. app-dev.sentinelops.example).',
    }).valueAsString;

    const hostedZone = route53.HostedZone.fromHostedZoneAttributes(this, 'HostedZone', {
      hostedZoneId,
      zoneName: hostedZoneName,
    });

    const frontendCertificate = new certificatemanager.Certificate(this, 'FrontendCertificate', {
      domainName: frontendDomainName,
      validation: certificatemanager.CertificateValidation.fromDns(hostedZone),
    });

    const frontendHttpsListener = frontendAlb.addListener('HttpsListener', {
      port: 443,
      certificates: [frontendCertificate],
      sslPolicy: elbv2.SslPolicy.RECOMMENDED_TLS,
      defaultAction: elbv2.ListenerAction.fixedResponse(403, { messageBody: 'Forbidden' }),
    });
    frontendHttpsListener.addAction('OriginVerifiedForward', {
      priority: 1,
      conditions: [elbv2.ListenerCondition.httpHeader('X-Origin-Verify', [originVerifySecret])],
      action: elbv2.ListenerAction.forward([frontendTargetGroup]),
    });
    frontendAlb.addListener('HttpRedirectListener', {
      port: 80,
      defaultAction: elbv2.ListenerAction.redirect({
        protocol: 'HTTPS',
        port: '443',
        permanent: true,
      }),
    });

    // --- WAF (CloudFront scope) -------------------------------------------------
    // Same AWS-managed rule groups + rate-based rule as ApiStack's REGIONAL
    // WAF for API Gateway (see api-stack.ts), but with `scope: 'CLOUDFRONT'`.
    // CLOUDFRONT-scope Web ACLs must be created in us-east-1 regardless of
    // where the protected distribution's stack deploys — this isn't a special
    // case here only because every environment's `env.region` is already
    // us-east-1 (see EnvironmentConfig.env's header comment); if that ever
    // changes, this WAF has to move to its own us-east-1-pinned stack the
    // same way the CloudFront ACM certificate would.
    const frontendWebAcl = new wafv2.CfnWebACL(this, 'FrontendWebAcl', {
      name: `sentinelops-${props.config.envName}-frontend-waf`,
      scope: 'CLOUDFRONT',
      defaultAction: { allow: {} },
      visibilityConfig: {
        sampledRequestsEnabled: true,
        cloudWatchMetricsEnabled: true,
        metricName: `sentinelops-${props.config.envName}-frontend-waf`,
      },
      rules: [
        {
          name: 'AWSManagedCommonRuleSet',
          priority: 0,
          overrideAction: { none: {} },
          statement: {
            managedRuleGroupStatement: { vendorName: 'AWS', name: 'AWSManagedRulesCommonRuleSet' },
          },
          visibilityConfig: {
            sampledRequestsEnabled: true,
            cloudWatchMetricsEnabled: true,
            metricName: 'CommonRuleSet',
          },
        },
        {
          name: 'RateLimitPerIp',
          priority: 1,
          action: { block: {} },
          statement: { rateBasedStatement: { limit: 2000, aggregateKeyType: 'IP' } },
          visibilityConfig: {
            sampledRequestsEnabled: true,
            cloudWatchMetricsEnabled: true,
            metricName: 'RateLimitPerIp',
          },
        },
      ],
    });

    // --- CloudFront ------------------------------------------------------------
    const distribution = new cloudfront.Distribution(this, 'FrontendDistribution', {
      comment: `sentinelops-${props.config.envName}-frontend`,
      domainNames: [frontendDomainName],
      certificate: frontendCertificate,
      webAclId: frontendWebAcl.attrArn,
      defaultBehavior: {
        origin: new cloudfrontOrigins.LoadBalancerV2Origin(frontendAlb, {
          protocolPolicy: cloudfront.OriginProtocolPolicy.HTTPS_ONLY,
          customHeaders: { 'X-Origin-Verify': originVerifySecret },
        }),
        viewerProtocolPolicy: cloudfront.ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
        cachePolicy: cloudfront.CachePolicy.CACHING_DISABLED, // apps/web is server-rendered, not static assets
        allowedMethods: cloudfront.AllowedMethods.ALLOW_ALL,
        originRequestPolicy: cloudfront.OriginRequestPolicy.ALL_VIEWER,
      },
    });

    new route53.ARecord(this, 'FrontendAliasRecord', {
      zone: hostedZone,
      recordName: frontendDomainName,
      target: route53.RecordTarget.fromAlias(new route53Targets.CloudFrontTarget(distribution)),
    });

    new cdk.CfnOutput(this, 'FrontendUrl', { value: `https://${frontendDomainName}` });
    new cdk.CfnOutput(this, 'FrontendDistributionDomainName', {
      value: distribution.distributionDomainName,
    });
    new cdk.CfnOutput(this, 'FrontendRepositoryUri', { value: frontendRepository.repositoryUri });
  }
}
