import * as cdk from 'aws-cdk-lib/core';
import * as ec2 from 'aws-cdk-lib/aws-ec2';
import * as ecr from 'aws-cdk-lib/aws-ecr';
import * as ecs from 'aws-cdk-lib/aws-ecs';
import * as elbv2 from 'aws-cdk-lib/aws-elasticloadbalancingv2';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as logs from 'aws-cdk-lib/aws-logs';
import * as kms from 'aws-cdk-lib/aws-kms';
import * as s3 from 'aws-cdk-lib/aws-s3';
import * as dynamodb from 'aws-cdk-lib/aws-dynamodb';
import * as events from 'aws-cdk-lib/aws-events';
import * as targets from 'aws-cdk-lib/aws-events-targets';
import * as cognito from 'aws-cdk-lib/aws-cognito';
import * as secretsmanager from 'aws-cdk-lib/aws-secretsmanager';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import { SqsEventSource } from 'aws-cdk-lib/aws-lambda-event-sources';
import * as sqs from 'aws-cdk-lib/aws-sqs';
import * as apigatewayv2 from 'aws-cdk-lib/aws-apigatewayv2';
import * as apigatewayv2Integrations from 'aws-cdk-lib/aws-apigatewayv2-integrations';
import * as wafv2 from 'aws-cdk-lib/aws-wafv2';
import * as certificatemanager from 'aws-cdk-lib/aws-certificatemanager';
import * as route53 from 'aws-cdk-lib/aws-route53';
import * as route53Targets from 'aws-cdk-lib/aws-route53-targets';
import { Construct } from 'constructs';
import * as path from 'path';
import { EnvironmentConfig } from '../config/environments';

const WORKERS_DIR = path.join(__dirname, '..', '..', '..', 'apps', 'workers');

export interface ApiStackProps extends cdk.StackProps {
  config: EnvironmentConfig;
  vpc: ec2.IVpc;
  albSecurityGroup: ec2.ISecurityGroup;
  vpcLinkSecurityGroup: ec2.ISecurityGroup;
  ecsSecurityGroup: ec2.ISecurityGroup;
  dbSecret: secretsmanager.ISecret;
  userPool: cognito.IUserPool;
  userPoolClient: cognito.IUserPoolClient;
  attachmentsBucket: s3.IBucket;
  reportsBucket: s3.IBucket;
  connectionsTable: dynamodb.ITable;
  eventBus: events.IEventBus;
  dataKey: kms.IKey;
  apiLogGroup: logs.ILogGroup;
  // Defaults to true (Route53 domain + ACM cert + API Gateway custom domain). Pass false
  // when no real hosted zone exists yet: the ALB's (private) HTTPS listener drops to
  // plain HTTP since there's no cert to terminate it with, and apiUrl falls back to
  // API Gateway's own AWS-issued HTTPS endpoint.
  deployCustomDomain?: boolean;
}

export class ApiStack extends cdk.Stack {
  public readonly apiUrl: string;

  constructor(scope: Construct, id: string, props: ApiStackProps) {
    super(scope, id, props);

    const { vpc, ecsSecurityGroup, dbSecret, userPool, userPoolClient, eventBus } = props;

    // --- apps/api compute: ECS Fargate --------------------------------------
    const apiRepository = new ecr.Repository(this, 'ApiRepository', {
      repositoryName: 'sentinelops-api',
      imageScanOnPush: true,
      encryption: ecr.RepositoryEncryption.KMS,
      encryptionKey: props.dataKey,
      removalPolicy: props.config.removalPolicy.compute,
    });
    new cdk.CfnOutput(this, 'ApiRepositoryUri', { value: apiRepository.repositoryUri });

    const apiCluster = new ecs.Cluster(this, 'SentinelOpsCluster', {
      vpc,
      clusterName: 'sentinelops-cluster',
      containerInsightsV2: ecs.ContainerInsights.ENABLED,
    });

    // Execution role: what ECS itself needs (image pull, logs, DB secret at container
    // start). Task role: the app's own runtime AWS SDK identity.
    const apiExecutionRole = new iam.Role(this, 'ApiExecutionRole', {
      assumedBy: new iam.ServicePrincipal('ecs-tasks.amazonaws.com'),
    });
    apiRepository.grantPull(apiExecutionRole);
    dbSecret.grantRead(apiExecutionRole);

    const apiTaskRole = new iam.Role(this, 'ApiTaskRole', {
      assumedBy: new iam.ServicePrincipal('ecs-tasks.amazonaws.com'),
    });
    props.attachmentsBucket.grantReadWrite(apiTaskRole);
    props.reportsBucket.grantReadWrite(apiTaskRole);
    eventBus.grantPutEventsTo(apiTaskRole);

    const apiTaskDefinition = new ecs.FargateTaskDefinition(this, 'ApiTaskDefinition', {
      family: 'sentinelops-api',
      cpu: 512,
      memoryLimitMiB: 1024,
      executionRole: apiExecutionRole,
      taskRole: apiTaskRole,
    });
    // CD passes -c apiImageTag=$GITHUB_SHA; ad-hoc synth/deploy falls back to `latest`.
    const apiImageTag = this.node.tryGetContext('apiImageTag') ?? 'latest';
    apiTaskDefinition.addContainer('ApiContainer', {
      image: ecs.ContainerImage.fromEcrRepository(apiRepository, apiImageTag),
      logging: ecs.LogDrivers.awsLogs({ streamPrefix: 'api', logGroup: props.apiLogGroup }),
      environment: {
        ASPNETCORE_ENVIRONMENT: 'Production',
        ASPNETCORE_URLS: 'http://+:5000',
        Cognito__Region: this.region,
        Cognito__UserPoolId: userPool.userPoolId,
        Cognito__ClientId: userPoolClient.userPoolClientId,
        Aws__EventBridge__EventBusName: eventBus.eventBusName,
        Aws__Attachments__BucketName: props.attachmentsBucket.bucketName,
        Aws__Reports__BucketName: props.reportsBucket.bucketName,
        // ASP.NET Core binds Cors:AllowedOrigins from Cors__AllowedOrigins__0, __1, ...
        // (see Program.cs). Without this, the API only allows localhost.
        ...Object.fromEntries(
          props.config.corsAllowedOrigins.map((origin, i) => [`Cors__AllowedOrigins__${i}`, origin]),
        ),
      },
      secrets: {
        DB_SECRET_JSON: ecs.Secret.fromSecretsManager(dbSecret),
      },
      portMappings: [{ containerPort: 5000 }],
      // Must match Program.cs's HostOptions.ShutdownTimeout: ECS sends SIGTERM and
      // waits this long before SIGKILL. A shorter value here would kill requests the
      // app still thinks it has time to finish.
      stopTimeout: cdk.Duration.seconds(30),
    });

    // --- X-Ray tracing ---------------------------------------------------------
    // Fargate has no host-level X-Ray daemon, so it runs as its own container in the
    // task; Program.cs sends segments to 127.0.0.1:2000, which this container relays.
    apiTaskDefinition.addContainer('XRayDaemonContainer', {
      image: ecs.ContainerImage.fromRegistry('public.ecr.aws/xray/aws-xray-daemon:latest'),
      cpu: 32,
      memoryLimitMiB: 256,
      essential: false,
      portMappings: [{ containerPort: 2000, protocol: ecs.Protocol.UDP }],
      logging: ecs.LogDrivers.awsLogs({ streamPrefix: 'xray', logGroup: props.apiLogGroup }),
    });
    // Task role, not execution role: the daemon needs xray:PutTraceSegments/PutTelemetryRecords.
    apiTaskRole.addManagedPolicy(
      iam.ManagedPolicy.fromAwsManagedPolicyName('AWSXRayDaemonWriteAccess'),
    );

    const apiService = new ecs.FargateService(this, 'ApiService', {
      cluster: apiCluster,
      taskDefinition: apiTaskDefinition,
      desiredCount: props.config.ecs.apiDesiredCount,
      securityGroups: [ecsSecurityGroup],
      vpcSubnets: { subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS },
      assignPublicIp: false,
      minHealthyPercent: 100,
      circuitBreaker: { rollback: true },
      // Time for a cold-started container to pass its first ALB health check.
      healthCheckGracePeriod: cdk.Duration.seconds(60),
    });

    // Target tracking on CPU, bounded by [apiDesiredCount, apiMaxCapacity].
    apiService
      .autoScaleTaskCount({
        minCapacity: props.config.ecs.apiDesiredCount,
        maxCapacity: props.config.ecs.apiMaxCapacity,
      })
      .scaleOnCpuUtilization('ApiCpuScaling', {
        targetUtilizationPercent: 60,
        scaleInCooldown: cdk.Duration.seconds(60),
        scaleOutCooldown: cdk.Duration.seconds(60),
      });

    // --- Private ALB ---------------------------------------------------------
    // Not internet-facing: API Gateway (below) is the public entry point, reaching
    // this ALB only through a VpcLink. albSecurityGroup accepts traffic only from
    // vpcLinkSecurityGroup.
    const apiAlb = new elbv2.ApplicationLoadBalancer(this, 'ApiAlb', {
      loadBalancerName: 'sentinelops-api-alb',
      vpc,
      internetFacing: false,
      securityGroup: props.albSecurityGroup,
      vpcSubnets: { subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS },
    });

    const apiTargetGroup = new elbv2.ApplicationTargetGroup(this, 'ApiTargetGroup', {
      vpc,
      port: 5000,
      protocol: elbv2.ApplicationProtocol.HTTP,
      targetType: elbv2.TargetType.IP,
      targets: [apiService],
      healthCheck: { path: '/healthz', healthyHttpCodes: '200' },
      // Shorter than the container's 30s stopTimeout so draining finishes before ECS
      // SIGKILLs the container.
      deregistrationDelay: cdk.Duration.seconds(20),
    });

    // --- DNS + ACM (regional, for the ALB/API Gateway) ------------------------
    // fromHostedZoneAttributes (not fromLookup) so cdk synth works with zero AWS
    // credentials, using the CfnParameter values below instead of a live Route53 call.
    const deployCustomDomain = props.deployCustomDomain ?? true;

    let apiAlbListener: elbv2.ApplicationListener;
    let apiDomain: apigatewayv2.DomainName | undefined;

    if (deployCustomDomain) {
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
      const apiDomainName = new cdk.CfnParameter(this, 'ApiDomainName', {
        type: 'String',
        default: props.config.domains.apiDomainNameDefault,
        description: 'Public domain name for the API (e.g. api-dev.sentinelops.example).',
      }).valueAsString;

      const hostedZone = route53.HostedZone.fromHostedZoneAttributes(this, 'HostedZone', {
        hostedZoneId,
        zoneName: hostedZoneName,
      });

      const apiCertificate = new certificatemanager.Certificate(this, 'ApiCertificate', {
        domainName: apiDomainName,
        validation: certificatemanager.CertificateValidation.fromDns(hostedZone),
      });

      apiAlbListener = apiAlb.addListener('HttpsListener', {
        port: 443,
        certificates: [apiCertificate],
        sslPolicy: elbv2.SslPolicy.RECOMMENDED_TLS,
        defaultTargetGroups: [apiTargetGroup],
      });
      apiAlb.addListener('HttpRedirectListener', {
        port: 80,
        defaultAction: elbv2.ListenerAction.redirect({
          protocol: 'HTTPS',
          port: '443',
          permanent: true,
        }),
      });

      apiDomain = new apigatewayv2.DomainName(this, 'ApiGatewayDomainName', {
        domainName: apiDomainName,
        certificate: apiCertificate,
      });
      new route53.ARecord(this, 'ApiAliasRecord', {
        zone: hostedZone,
        recordName: apiDomainName,
        target: route53.RecordTarget.fromAlias(
          new route53Targets.ApiGatewayv2DomainProperties(
            apiDomain.regionalDomainName,
            apiDomain.regionalHostedZoneId,
          ),
        ),
      });
    } else {
      // No hosted zone, so no domain to issue an ACM cert for. This hop is inside the
      // VPC and security-group-restricted regardless, so plain HTTP is the same trust
      // boundary the HTTPS path would have had.
      apiAlbListener = apiAlb.addListener('HttpListener', {
        port: 80,
        protocol: elbv2.ApplicationProtocol.HTTP,
        defaultTargetGroups: [apiTargetGroup],
      });
    }

    // --- API Gateway (HTTP API) + VpcLink ------------------------------------
    // Public entry point for the API. The VpcLink's ENIs (in vpcLinkSecurityGroup)
    // are the only thing the private ALB accepts traffic from.
    const vpcLink = new apigatewayv2.VpcLink(this, 'ApiVpcLink', {
      vpc,
      subnets: { subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS },
      securityGroups: [props.vpcLinkSecurityGroup],
    });

    const httpApi = new apigatewayv2.HttpApi(this, 'ApiHttpApi', {
      apiName: 'sentinelops-api',
      defaultIntegration: new apigatewayv2Integrations.HttpAlbIntegration(
        'ApiAlbIntegration',
        apiAlbListener,
        { vpcLink },
      ),
    });

    if (apiDomain) {
      new apigatewayv2.ApiMapping(this, 'ApiGatewayMapping', {
        api: httpApi,
        domainName: apiDomain,
      });
    }

    // --- WAF (on the API Gateway) --------------------------
    // AWS-managed rule groups for common web exploits + SQLi, plus a rate-based rule as
    // a coarse backstop to Program.cs's app-level rate limiter.
    // API Gateway REST/HTTP APIs use REGIONAL scope; only CloudFront needs CLOUDFRONT.
    const apiWebAcl = new wafv2.CfnWebACL(this, 'ApiWebAcl', {
      name: 'sentinelops-api-waf',
      scope: 'REGIONAL',
      defaultAction: { allow: {} },
      visibilityConfig: {
        sampledRequestsEnabled: true,
        cloudWatchMetricsEnabled: true,
        metricName: 'sentinelops-api-waf',
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
          name: 'AWSManagedSQLiRuleSet',
          priority: 1,
          overrideAction: { none: {} },
          statement: {
            managedRuleGroupStatement: { vendorName: 'AWS', name: 'AWSManagedRulesSQLiRuleSet' },
          },
          visibilityConfig: {
            sampledRequestsEnabled: true,
            cloudWatchMetricsEnabled: true,
            metricName: 'SQLiRuleSet',
          },
        },
        {
          name: 'RateLimitPerIp',
          priority: 2,
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
    // WAFv2 association needs the stage ARN, not the API's own ARN. defaultStage has no
    // typed stageArn property, so build it manually.
    const defaultStageName = httpApi.defaultStage!.stageName;
    const httpApiStageArn = `arn:${cdk.Aws.PARTITION}:apigateway:${this.region}::/apis/${httpApi.apiId}/stages/${defaultStageName}`;
    new wafv2.CfnWebACLAssociation(this, 'ApiWebAclAssociation', {
      resourceArn: httpApiStageArn,
      webAclArn: apiWebAcl.attrArn,
    });

    this.apiUrl = apiDomain ? `https://${apiDomain.name}` : httpApi.apiEndpoint!;
    new cdk.CfnOutput(this, 'ApiUrl', { value: this.apiUrl });
    new cdk.CfnOutput(this, 'ApiHttpApiEndpoint', {
      value: httpApi.apiEndpoint,
      description: 'Fallback API Gateway-generated URL, usable before DNS/cert are live.',
    });

    // --- Real-time dashboard: WebSocket API + Connect/Disconnect/Broadcast ---
    // SentinelOps.Workers.Dashboard publishes as one package but deploys as three
    // Lambda functions. Connect/Disconnect are WebSocket route targets; Broadcast is
    // SQS-triggered.
    const dashboardCodeAsset = lambda.Code.fromAsset(
      path.join(
        WORKERS_DIR,
        'SentinelOps.Workers.Dashboard',
        'bin',
        'Release',
        'net10.0',
        'publish',
      ),
    );

    const dashboardFunction = (
      name: string,
      handlerClass: string,
      opts: { extraEnv?: Record<string, string>; needsDatabase?: boolean } = {},
    ) =>
      new lambda.Function(this, `Dashboard${name}Function`, {
        functionName: `sentinelops-dashboard-${kebab(name)}`,
        runtime: lambda.Runtime.DOTNET_10,
        architecture: lambda.Architecture.X86_64,
        handler: `SentinelOps.Workers.Dashboard::SentinelOps.Workers.Dashboard.${handlerClass}::FunctionHandler`,
        code: dashboardCodeAsset,
        memorySize: 256,
        timeout: cdk.Duration.seconds(15),
        reservedConcurrentExecutions: 10,
        logGroup: new logs.LogGroup(this, `Dashboard${name}LogGroup`, {
          logGroupName: `/aws/lambda/sentinelops-dashboard-${kebab(name)}`,
          retention: props.config.logRetention,
          removalPolicy: props.config.removalPolicy.compute,
        }),
        tracing: lambda.Tracing.ACTIVE,
        // Only ConnectFunction talks to Postgres; Disconnect/Broadcast stay outside the
        // VPC to avoid ENI/cold-start cost.
        ...(opts.needsDatabase
          ? {
              vpc,
              vpcSubnets: { subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS },
              securityGroups: [ecsSecurityGroup],
            }
          : {}),
        environment: {
          // AWS_REGION is a Lambda-reserved variable populated automatically at runtime.
          CONNECTIONS_TABLE_NAME: props.connectionsTable.tableName,
          ...opts.extraEnv,
        },
      });

    const dashboardConnectFn = dashboardFunction('Connect', 'ConnectFunction', {
      needsDatabase: true,
      extraEnv: {
        DB_SECRET_ARN: dbSecret.secretArn,
        COGNITO_REGION: this.region,
        COGNITO_USER_POOL_ID: userPool.userPoolId,
        COGNITO_CLIENT_ID: userPoolClient.userPoolClientId,
      },
    });
    props.connectionsTable.grantReadWriteData(dashboardConnectFn);
    dbSecret.grantRead(dashboardConnectFn);

    const dashboardDisconnectFn = dashboardFunction('Disconnect', 'DisconnectFunction');
    props.connectionsTable.grantReadWriteData(dashboardDisconnectFn);

    const dashboardWebSocketApi = new apigatewayv2.WebSocketApi(this, 'DashboardWebSocketApi', {
      apiName: 'sentinelops-dashboard',
      connectRouteOptions: {
        integration: new apigatewayv2Integrations.WebSocketLambdaIntegration(
          'DashboardConnectIntegration',
          dashboardConnectFn,
        ),
      },
      disconnectRouteOptions: {
        integration: new apigatewayv2Integrations.WebSocketLambdaIntegration(
          'DashboardDisconnectIntegration',
          dashboardDisconnectFn,
        ),
      },
    });
    const dashboardWebSocketStage = new apigatewayv2.WebSocketStage(
      this,
      'DashboardWebSocketStage',
      {
        webSocketApi: dashboardWebSocketApi,
        stageName: 'prod',
        autoDeploy: true,
      },
    );

    new cdk.CfnOutput(this, 'DashboardWebSocketUrl', {
      value: dashboardWebSocketStage.url,
      description:
        'wss:// URL the dashboard connects to, as ?orgId={orgId}&token={cognitoAccessToken}',
    });

    const dashboardBroadcastDlq = new sqs.Queue(this, 'DashboardBroadcastDlq', {
      queueName: 'sentinelops-dashboard-broadcast-dlq',
      retentionPeriod: cdk.Duration.days(14),
    });
    const dashboardBroadcastQueue = new sqs.Queue(this, 'DashboardBroadcastQueue', {
      queueName: 'sentinelops-dashboard-broadcast',
      visibilityTimeout: cdk.Duration.seconds(15),
      retentionPeriod: cdk.Duration.days(4),
      deadLetterQueue: { queue: dashboardBroadcastDlq, maxReceiveCount: 5 },
    });
    new cdk.CfnOutput(this, 'DashboardBroadcastQueueUrl', {
      value: dashboardBroadcastQueue.queueUrl,
    });

    const dashboardBroadcastFn = dashboardFunction('Broadcast', 'BroadcastFunction', {
      extraEnv: { WEBSOCKET_MANAGEMENT_ENDPOINT: dashboardWebSocketStage.callbackUrl },
    });
    dashboardBroadcastFn.addEventSource(
      new SqsEventSource(dashboardBroadcastQueue, { batchSize: 10, reportBatchItemFailures: true }),
    );
    props.connectionsTable.grantReadWriteData(dashboardBroadcastFn);
    dashboardWebSocketApi.grantManageConnections(dashboardBroadcastFn);

    // Only the event types the dashboard actually renders.
    new events.Rule(this, 'DashboardBroadcastRule', {
      eventBus,
      eventPattern: {
        detailType: ['incident.created', 'incident.updated', 'incident.resolved', 'alert.received'],
      },
      targets: [new targets.SqsQueue(dashboardBroadcastQueue)],
    });
  }
}

function kebab(pascalCase: string): string {
  return pascalCase.replace(/([a-z0-9])([A-Z])/g, '$1-$2').toLowerCase();
}
