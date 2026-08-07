import * as cdk from 'aws-cdk-lib/core';
import * as cognito from 'aws-cdk-lib/aws-cognito';
import * as sqs from 'aws-cdk-lib/aws-sqs';
import * as events from 'aws-cdk-lib/aws-events';
import * as targets from 'aws-cdk-lib/aws-events-targets';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import { SqsEventSource } from 'aws-cdk-lib/aws-lambda-event-sources';
import { Construct } from 'constructs';
import * as path from 'path';

// One queue + one DLQ per background worker (see section 11/12 of the
// checklist and the "Event chain design" section of the implementation plan
// for why this is 7 queues rather than the 3 named in the raw checklist text —
// two Lambdas can't safely split one shared queue by message type).
interface WorkerQueue {
  queue: sqs.Queue;
  dlq: sqs.Queue;
}

// Publish path for `dotnet lambda package` / `dotnet publish` for each worker
// project — not built by this stack; run `dotnet publish -c Release` in the
// worker's directory before `cdk deploy`.
const WORKERS_DIR = path.join(__dirname, '..', '..', 'apps', 'workers');

export class InfrastructureStack extends cdk.Stack {
  constructor(scope: Construct, id: string, props?: cdk.StackProps) {
    super(scope, id, props);

    const userPool = new cognito.UserPool(this, 'SentinelOpsUserPool', {
      userPoolName: 'sentinelops-users',
      selfSignUpEnabled: true,
      signInAliases: { email: true, username: false },
      autoVerify: { email: true },
      standardAttributes: {
        email: { required: true, mutable: true },
      },
      passwordPolicy: {
        minLength: 8,
        requireLowercase: true,
        requireUppercase: true,
        requireDigits: true,
        requireSymbols: true,
        tempPasswordValidity: cdk.Duration.days(3),
      },
      accountRecovery: cognito.AccountRecovery.EMAIL_ONLY,
      mfa: cognito.Mfa.OPTIONAL,
      mfaSecondFactor: { sms: false, otp: true },
      userVerification: {
        emailSubject: 'Verify your SentinelOps account',
        emailBody:
          'Welcome to SentinelOps. Your verification code is {####}.',
        emailStyle: cognito.VerificationEmailStyle.CODE,
      },
      removalPolicy: cdk.RemovalPolicy.RETAIN,
    });

    const userPoolClient = new cognito.UserPoolClient(this, 'SentinelOpsUserPoolClient', {
      userPool,
      userPoolClientName: 'sentinelops-api',
      generateSecret: false,
      authFlows: {
        userPassword: true,
      },
      preventUserExistenceErrors: true,
      accessTokenValidity: cdk.Duration.hours(1),
      idTokenValidity: cdk.Duration.hours(1),
      refreshTokenValidity: cdk.Duration.days(30),
    });

    new cdk.CfnOutput(this, 'UserPoolId', {
      value: userPool.userPoolId,
      description: 'Cognito User Pool ID — set as Cognito__UserPoolId on apps/api',
    });

    new cdk.CfnOutput(this, 'UserPoolClientId', {
      value: userPoolClient.userPoolClientId,
      description: 'Cognito User Pool Client ID — set as Cognito__ClientId on apps/api',
    });

    new cdk.CfnOutput(this, 'Region', {
      value: this.region,
      description: 'AWS region — set as Cognito__Region on apps/api',
    });

    // --- Event-driven backbone -------------------------------------------
    // Custom bus (not the default bus) so alert/incident/notification events
    // are isolated from anything else running in this account, and so rules
    // here can safely use wildcard source/detail-type matches (analytics,
    // audit-log) without catching unrelated traffic.
    const eventBus = new events.EventBus(this, 'SentinelOpsEventBus', {
      eventBusName: 'sentinelops-events',
    });

    new cdk.CfnOutput(this, 'EventBusName', {
      value: eventBus.eventBusName,
      description: 'EventBridge bus name — set as Aws__EventBridge__EventBusName on apps/api',
    });
    new cdk.CfnOutput(this, 'EventBusArn', {
      value: eventBus.eventBusArn,
      description: 'EventBridge bus ARN',
    });

    const workerQueue = (name: string, visibilityTimeoutSeconds: number): WorkerQueue => {
      const dlq = new sqs.Queue(this, `${name}Dlq`, {
        queueName: `sentinelops-${kebab(name)}-dlq`,
        retentionPeriod: cdk.Duration.days(14),
      });
      const queue = new sqs.Queue(this, `${name}Queue`, {
        queueName: `sentinelops-${kebab(name)}`,
        visibilityTimeout: cdk.Duration.seconds(visibilityTimeoutSeconds),
        retentionPeriod: cdk.Duration.days(4),
        deadLetterQueue: { queue: dlq, maxReceiveCount: 5 },
      });
      new cdk.CfnOutput(this, `${name}QueueUrl`, { value: queue.queueUrl });
      return { queue, dlq };
    };

    const dbConnectionString = new cdk.CfnParameter(this, 'DbConnectionString', {
      type: 'String',
      noEcho: true,
      description:
        'Npgsql connection string the workers use — same database as apps/api, ' +
        'passed at deploy time (not committed) since this stack has no VPC/RDS of its own yet.',
    }).valueAsString;

    const workerFunction = (
      name: string,
      projectDirName: string,
      queue: sqs.Queue,
      opts: { memoryMb?: number; timeoutSeconds?: number; reservedConcurrency?: number; extraEnv?: Record<string, string> } = {},
    ) => {
      const fn = new lambda.Function(this, `${name}Function`, {
        functionName: `sentinelops-${kebab(name)}`,
        runtime: lambda.Runtime.DOTNET_10,
        architecture: lambda.Architecture.X86_64,
        handler: `SentinelOps.Workers.${name}::SentinelOps.Workers.${name}.Function::FunctionHandler`,
        code: lambda.Code.fromAsset(path.join(WORKERS_DIR, projectDirName, 'bin', 'Release', 'net10.0', 'publish')),
        memorySize: opts.memoryMb ?? 512,
        timeout: cdk.Duration.seconds(opts.timeoutSeconds ?? 30),
        // Conservative on purpose — every worker shares the same Postgres
        // instance apps/api uses, so this caps how many concurrent
        // connections a burst of messages can open.
        reservedConcurrentExecutions: opts.reservedConcurrency ?? 5,
        environment: {
          CONNECTION_STRING: dbConnectionString,
          EVENT_BUS_NAME: eventBus.eventBusName,
          ...opts.extraEnv,
        },
      });
      fn.addEventSource(new SqsEventSource(queue, { batchSize: 10, reportBatchItemFailures: true }));
      eventBus.grantPutEventsTo(fn);
      return fn;
    };

    const alertValidation = workerQueue('AlertValidation', 30);
    const deduplication = workerQueue('Deduplication', 30);
    const incidentCreation = workerQueue('IncidentCreation', 30);
    const responderAssignment = workerQueue('ResponderAssignment', 30);
    const notification = workerQueue('Notification', 15);
    const analytics = workerQueue('Analytics', 15);
    const auditLog = workerQueue('AuditLog', 15);

    new events.Rule(this, 'AlertReceivedRule', {
      eventBus,
      eventPattern: { detailType: ['alert.received'] },
      targets: [new targets.SqsQueue(alertValidation.queue)],
    });
    new events.Rule(this, 'AlertValidatedRule', {
      eventBus,
      eventPattern: { detailType: ['alert.validated'] },
      targets: [new targets.SqsQueue(deduplication.queue)],
    });
    // No rule targets incidentCreation.queue: it's fed only by a direct
    // SendMessage from the deduplication worker once an alert is confirmed
    // unique (see IncidentCreationRequest in SentinelOps.Events) — routing
    // that decision through EventBridge as a second `alert.validated`
    // listener would race with the deduplication worker's own decision.
    new events.Rule(this, 'IncidentCreatedRule', {
      eventBus,
      eventPattern: { detailType: ['incident.created'] },
      targets: [new targets.SqsQueue(responderAssignment.queue)],
    });
    new events.Rule(this, 'NotificationRequestedRule', {
      eventBus,
      eventPattern: { detailType: ['notification.requested'] },
      targets: [new targets.SqsQueue(notification.queue)],
    });
    // Fan-out to the two pure-observer workers: every one of the 11 event types
    // this system defines (see infrastructure/event-schemas/). CDK's typed
    // EventPattern.detailType only accepts literal strings, not a true
    // wildcard/prefix matcher, so this is spelled out explicitly rather than
    // matched structurally — which also means a 12th event type added later
    // must be added here too, deliberately (nothing silently starts flowing
    // to analytics/audit-log without a one-line review of this list).
    const allEventsPattern: events.EventPattern = {
      detailType: [
        'alert.received',
        'alert.validated',
        'alert.rejected',
        'incident.created',
        'incident.updated',
        'incident.acknowledged',
        'incident.escalated',
        'incident.resolved',
        'notification.requested',
        'notification.delivered',
        'notification.failed',
      ],
    };
    new events.Rule(this, 'AllEventsToAnalyticsRule', {
      eventBus,
      eventPattern: allEventsPattern,
      targets: [new targets.SqsQueue(analytics.queue)],
    });
    new events.Rule(this, 'AllEventsToAuditLogRule', {
      eventBus,
      eventPattern: allEventsPattern,
      targets: [new targets.SqsQueue(auditLog.queue)],
    });

    workerFunction('AlertValidation', 'SentinelOps.Workers.AlertValidation', alertValidation.queue);

    const dedupFn = workerFunction('Deduplication', 'SentinelOps.Workers.Deduplication', deduplication.queue, {
      extraEnv: { INCIDENT_CREATION_QUEUE_URL: incidentCreation.queue.queueUrl },
    });
    incidentCreation.queue.grantSendMessages(dedupFn);

    workerFunction('IncidentCreation', 'SentinelOps.Workers.IncidentCreation', incidentCreation.queue);
    workerFunction('ResponderAssignment', 'SentinelOps.Workers.ResponderAssignment', responderAssignment.queue);
    workerFunction('Notification', 'SentinelOps.Workers.Notification', notification.queue, {
      memoryMb: 256,
      timeoutSeconds: 15,
    });
    workerFunction('Analytics', 'SentinelOps.Workers.Analytics', analytics.queue, {
      memoryMb: 256,
      timeoutSeconds: 15,
      reservedConcurrency: 10,
    });
    workerFunction('AuditLog', 'SentinelOps.Workers.AuditLog', auditLog.queue, {
      memoryMb: 256,
      timeoutSeconds: 15,
      reservedConcurrency: 10,
    });
  }
}

function kebab(pascalCase: string): string {
  return pascalCase.replace(/([a-z0-9])([A-Z])/g, '$1-$2').toLowerCase();
}
