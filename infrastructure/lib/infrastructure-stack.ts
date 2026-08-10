import * as cdk from 'aws-cdk-lib/core';
import * as cognito from 'aws-cdk-lib/aws-cognito';
import * as sqs from 'aws-cdk-lib/aws-sqs';
import * as dynamodb from 'aws-cdk-lib/aws-dynamodb';
import * as ses from 'aws-cdk-lib/aws-ses';
import * as events from 'aws-cdk-lib/aws-events';
import * as targets from 'aws-cdk-lib/aws-events-targets';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import { SqsEventSource } from 'aws-cdk-lib/aws-lambda-event-sources';
import * as sfn from 'aws-cdk-lib/aws-stepfunctions';
import * as tasks from 'aws-cdk-lib/aws-stepfunctions-tasks';
import * as apigatewayv2 from 'aws-cdk-lib/aws-apigatewayv2';
import * as apigatewayv2Integrations from 'aws-cdk-lib/aws-apigatewayv2-integrations';
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

    // Active alert-fingerprint index for the deduplication engine (section 13):
    // one item per (org, normalized title, error code, service, environment)
    // hash, holding the incident it currently maps to. TTL-expired items just
    // stop matching — they aren't a durable record, Postgres is (Alert/
    // Incident rows), so PAY_PER_REQUEST plus TTL is enough here.
    const fingerprintTable = new dynamodb.Table(this, 'AlertFingerprintTable', {
      tableName: 'sentinelops-alert-fingerprints',
      partitionKey: { name: 'Fingerprint', type: dynamodb.AttributeType.STRING },
      billingMode: dynamodb.BillingMode.PAY_PER_REQUEST,
      timeToLiveAttribute: 'ExpiresAt',
      removalPolicy: cdk.RemovalPolicy.DESTROY,
    });

    new cdk.CfnOutput(this, 'AlertFingerprintTableName', { value: fingerprintTable.tableName });

    // --- Notification email (section 18) -----------------------------------
    // Domain verification via SES's own DKIM records rather than a
    // Route53-hosted-zone identity — this stack doesn't own a hosted zone, so
    // the three CNAME records below have to be added to the domain's DNS
    // manually before SES will accept sends from it.
    const notificationDomainName = new cdk.CfnParameter(this, 'NotificationDomainName', {
      type: 'String',
      description:
        'Domain SES sends incident emails from (e.g. alerts.example.com). Add the three ' +
        'EmailIdentityDkimRecord CNAME outputs to this domain\'s DNS to complete verification.',
    }).valueAsString;

    const notificationEmailIdentity = new ses.EmailIdentity(this, 'NotificationEmailIdentity', {
      identity: ses.Identity.domain(notificationDomainName),
    });

    const senderEmail = `alerts@${notificationDomainName}`;

    new cdk.CfnOutput(this, 'EmailIdentityDkimRecord1', {
      value: `${notificationEmailIdentity.dkimDnsTokenName1} CNAME ${notificationEmailIdentity.dkimDnsTokenValue1}`,
    });
    new cdk.CfnOutput(this, 'EmailIdentityDkimRecord2', {
      value: `${notificationEmailIdentity.dkimDnsTokenName2} CNAME ${notificationEmailIdentity.dkimDnsTokenValue2}`,
    });
    new cdk.CfnOutput(this, 'EmailIdentityDkimRecord3', {
      value: `${notificationEmailIdentity.dkimDnsTokenName3} CNAME ${notificationEmailIdentity.dkimDnsTokenValue3}`,
    });
    new cdk.CfnOutput(this, 'NotificationSenderEmail', { value: senderEmail });

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
    // Reopening an incident restarts escalation from the top — see the
    // IncidentUpdated/Reopened rule below.
    const escalationRestart = workerQueue('EscalationRestart', 30);

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
    // Content-filtered on the specific field/value IncidentsController and
    // the deduplication worker both use for a reopen (see
    // EscalationRestartFunction) — not every `incident.updated` event, just
    // the one that means "escalation needs to restart."
    new events.Rule(this, 'IncidentReopenedRule', {
      eventBus,
      eventPattern: {
        detailType: ['incident.updated'],
        detail: { field: ['Status'], newValue: ['Reopened'] },
      },
      targets: [new targets.SqsQueue(escalationRestart.queue)],
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
      extraEnv: {
        INCIDENT_CREATION_QUEUE_URL: incidentCreation.queue.queueUrl,
        FINGERPRINT_TABLE_NAME: fingerprintTable.tableName,
      },
    });
    incidentCreation.queue.grantSendMessages(dedupFn);
    fingerprintTable.grantReadWriteData(dedupFn);

    const incidentCreationFn = workerFunction('IncidentCreation', 'SentinelOps.Workers.IncidentCreation', incidentCreation.queue, {
      extraEnv: { FINGERPRINT_TABLE_NAME: fingerprintTable.tableName },
    });
    fingerprintTable.grantReadWriteData(incidentCreationFn);
    const responderAssignmentFn = workerFunction(
      'ResponderAssignment', 'SentinelOps.Workers.ResponderAssignment', responderAssignment.queue);
    const notificationFn = workerFunction('Notification', 'SentinelOps.Workers.Notification', notification.queue, {
      memoryMb: 256,
      timeoutSeconds: 15,
      extraEnv: { SES_SENDER_EMAIL: senderEmail },
    });
    notificationEmailIdentity.grantSendEmail(notificationFn);
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

    // --- Escalation state machine (section 15/17) -------------------------
    // SentinelOps.Workers.Escalation publishes as one package but deploys as
    // two Lambda functions with different handlers: EscalationFunction (a
    // direct Step Functions task target — no SQS queue, request/response)
    // and RestartFunction (SQS-triggered, on a reopened incident). Neither
    // fits the `workerFunction` factory's "one queue, one Function class per
    // project" assumption, so both are built by hand here.
    const escalationCodeAsset = lambda.Code.fromAsset(
      path.join(WORKERS_DIR, 'SentinelOps.Workers.Escalation', 'bin', 'Release', 'net10.0', 'publish'));

    const escalationFn = new lambda.Function(this, 'EscalationFunction', {
      functionName: 'sentinelops-escalation',
      runtime: lambda.Runtime.DOTNET_10,
      architecture: lambda.Architecture.X86_64,
      handler: 'SentinelOps.Workers.Escalation::SentinelOps.Workers.Escalation.EscalationFunction::FunctionHandler',
      code: escalationCodeAsset,
      memorySize: 512,
      timeout: cdk.Duration.seconds(30),
      reservedConcurrentExecutions: 5,
      environment: {
        CONNECTION_STRING: dbConnectionString,
        EVENT_BUS_NAME: eventBus.eventBusName,
      },
    });
    eventBus.grantPutEventsTo(escalationFn);

    const escalationRestartFn = new lambda.Function(this, 'EscalationRestartFunction', {
      functionName: 'sentinelops-escalation-restart',
      runtime: lambda.Runtime.DOTNET_10,
      architecture: lambda.Architecture.X86_64,
      handler: 'SentinelOps.Workers.Escalation::SentinelOps.Workers.Escalation.RestartFunction::FunctionHandler',
      code: escalationCodeAsset,
      memorySize: 512,
      timeout: cdk.Duration.seconds(30),
      reservedConcurrentExecutions: 5,
      environment: {
        CONNECTION_STRING: dbConnectionString,
        EVENT_BUS_NAME: eventBus.eventBusName,
      },
    });
    escalationRestartFn.addEventSource(new SqsEventSource(escalationRestart.queue, { batchSize: 10, reportBatchItemFailures: true }));
    eventBus.grantPutEventsTo(escalationRestartFn);

    // Every task both accepts and returns EscalationStateInput's shape
    // (see that record's doc comment) — `payloadResponseOnly` makes the
    // Lambda's JSON return value replace the whole state, so no Wait/Choice
    // state here ever needs to reshape or merge JSON paths.
    const escalationTaskPayload = (action: string) =>
      sfn.TaskInput.fromObject({
        action,
        organizationId: sfn.JsonPath.stringAt('$.organizationId'),
        incidentId: sfn.JsonPath.stringAt('$.incidentId'),
        correlationId: sfn.JsonPath.stringAt('$.correlationId'),
        escalationPolicyId: sfn.JsonPath.stringAt('$.escalationPolicyId'),
        currentLevelOrder: sfn.JsonPath.numberAt('$.currentLevelOrder'),
        ackTimeoutSeconds: sfn.JsonPath.numberAt('$.ackTimeoutSeconds'),
        fallbackNotified: sfn.JsonPath.stringAt('$.fallbackNotified'),
      });

    const recordFailureTask = new tasks.LambdaInvoke(this, 'RecordEscalationFailure', {
      lambdaFunction: escalationFn,
      payload: escalationTaskPayload('RecordFailure'),
      payloadResponseOnly: true,
    }).next(new sfn.Fail(this, 'EscalationWorkflowFailed'));

    const checkStatusTask = new tasks.LambdaInvoke(this, 'CheckIncidentStatus', {
      lambdaFunction: escalationFn,
      payload: escalationTaskPayload('CheckStatus'),
      payloadResponseOnly: true,
    });
    checkStatusTask.addCatch(recordFailureTask, { resultPath: '$.error' });

    const advanceLevelTask = new tasks.LambdaInvoke(this, 'AdvanceEscalationLevel', {
      lambdaFunction: escalationFn,
      payload: escalationTaskPayload('AdvanceLevel'),
      payloadResponseOnly: true,
    });
    advanceLevelTask.addCatch(recordFailureTask, { resultPath: '$.error' });

    const waitForAck = new sfn.Wait(this, 'WaitForAck', {
      time: sfn.WaitTime.secondsPath('$.ackTimeoutSeconds'),
    });

    const stoppedAcknowledgedOrResolved = new sfn.Succeed(this, 'StoppedAcknowledgedOrResolved');
    const stoppedEscalationExhausted = new sfn.Succeed(this, 'StoppedEscalationExhausted');

    checkStatusTask.next(
      new sfn.Choice(this, 'IsAcknowledgedOrResolved')
        .when(sfn.Condition.booleanEquals('$.acknowledgedOrResolved', true), stoppedAcknowledgedOrResolved)
        .otherwise(advanceLevelTask),
    );
    advanceLevelTask.next(
      new sfn.Choice(this, 'IsEscalationExhausted')
        .when(sfn.Condition.booleanEquals('$.stop', true), stoppedEscalationExhausted)
        .otherwise(waitForAck),
    );
    waitForAck.next(checkStatusTask);

    // Safety net: no single execution should run longer than this even if
    // every level + the fallback administrator uses the maximum 1440-minute
    // (24h) ack timeout the API allows per level.
    const escalationStateMachine = new sfn.StateMachine(this, 'EscalationStateMachine', {
      stateMachineName: 'sentinelops-incident-escalation',
      definitionBody: sfn.DefinitionBody.fromChainable(waitForAck),
      timeout: cdk.Duration.days(7),
    });

    new cdk.CfnOutput(this, 'EscalationStateMachineArn', { value: escalationStateMachine.stateMachineArn });

    escalationStateMachine.grantStartExecution(responderAssignmentFn);
    escalationStateMachine.grantStartExecution(escalationRestartFn);
    responderAssignmentFn.addEnvironment('ESCALATION_STATE_MACHINE_ARN', escalationStateMachine.stateMachineArn);
    escalationRestartFn.addEnvironment('ESCALATION_STATE_MACHINE_ARN', escalationStateMachine.stateMachineArn);

    // --- Real-time dashboard (section 19) ----------------------------------
    // One item per live WebSocket connection, queried by org via the GSI when
    // broadcasting — TTL is a backstop for connections that vanish without a
    // clean $disconnect (see BroadcastFunction's GoneException handling for
    // the normal path).
    const connectionsTable = new dynamodb.Table(this, 'DashboardConnectionsTable', {
      tableName: 'sentinelops-dashboard-connections',
      partitionKey: { name: 'ConnectionId', type: dynamodb.AttributeType.STRING },
      billingMode: dynamodb.BillingMode.PAY_PER_REQUEST,
      timeToLiveAttribute: 'ExpiresAt',
      removalPolicy: cdk.RemovalPolicy.DESTROY,
    });
    connectionsTable.addGlobalSecondaryIndex({
      indexName: 'OrganizationId-index',
      partitionKey: { name: 'OrganizationId', type: dynamodb.AttributeType.STRING },
    });

    // SentinelOps.Workers.Dashboard publishes as one package but deploys as
    // three Lambda functions with different handlers — same pattern as
    // SentinelOps.Workers.Escalation. Connect/Disconnect are API Gateway
    // WebSocket route targets (no SQS queue); Broadcast is SQS-triggered.
    const dashboardCodeAsset = lambda.Code.fromAsset(
      path.join(WORKERS_DIR, 'SentinelOps.Workers.Dashboard', 'bin', 'Release', 'net10.0', 'publish'));

    const dashboardFunction = (name: string, handlerClass: string, opts: { extraEnv?: Record<string, string> } = {}) =>
      new lambda.Function(this, `Dashboard${name}Function`, {
        functionName: `sentinelops-dashboard-${kebab(name)}`,
        runtime: lambda.Runtime.DOTNET_10,
        architecture: lambda.Architecture.X86_64,
        handler: `SentinelOps.Workers.Dashboard::SentinelOps.Workers.Dashboard.${handlerClass}::FunctionHandler`,
        code: dashboardCodeAsset,
        memorySize: 256,
        timeout: cdk.Duration.seconds(15),
        reservedConcurrentExecutions: 10,
        environment: {
          // AWS_REGION is a Lambda-reserved variable, populated automatically
          // at runtime — DynamoDbConnectionStore/CognitoTokenValidator read
          // it directly, no need to set it here.
          CONNECTIONS_TABLE_NAME: connectionsTable.tableName,
          ...opts.extraEnv,
        },
      });

    const dashboardConnectFn = dashboardFunction('Connect', 'ConnectFunction', {
      extraEnv: {
        CONNECTION_STRING: dbConnectionString,
        COGNITO_REGION: this.region,
        COGNITO_USER_POOL_ID: userPool.userPoolId,
        COGNITO_CLIENT_ID: userPoolClient.userPoolClientId,
      },
    });
    connectionsTable.grantReadWriteData(dashboardConnectFn);

    const dashboardDisconnectFn = dashboardFunction('Disconnect', 'DisconnectFunction');
    connectionsTable.grantReadWriteData(dashboardDisconnectFn);

    const dashboardWebSocketApi = new apigatewayv2.WebSocketApi(this, 'DashboardWebSocketApi', {
      apiName: 'sentinelops-dashboard',
      connectRouteOptions: {
        integration: new apigatewayv2Integrations.WebSocketLambdaIntegration('DashboardConnectIntegration', dashboardConnectFn),
      },
      disconnectRouteOptions: {
        integration: new apigatewayv2Integrations.WebSocketLambdaIntegration('DashboardDisconnectIntegration', dashboardDisconnectFn),
      },
    });
    const dashboardWebSocketStage = new apigatewayv2.WebSocketStage(this, 'DashboardWebSocketStage', {
      webSocketApi: dashboardWebSocketApi,
      stageName: 'prod',
      autoDeploy: true,
    });

    new cdk.CfnOutput(this, 'DashboardWebSocketUrl', {
      value: dashboardWebSocketStage.url,
      description: 'wss:// URL the dashboard connects to, as ?orgId={orgId}&token={cognitoAccessToken}',
    });

    const dashboard = workerQueue('DashboardBroadcast', 15);
    const dashboardBroadcastFn = dashboardFunction('Broadcast', 'BroadcastFunction', {
      extraEnv: { WEBSOCKET_MANAGEMENT_ENDPOINT: dashboardWebSocketStage.callbackUrl },
    });
    dashboardBroadcastFn.addEventSource(new SqsEventSource(dashboard.queue, { batchSize: 10, reportBatchItemFailures: true }));
    connectionsTable.grantReadWriteData(dashboardBroadcastFn);
    dashboardWebSocketApi.grantManageConnections(dashboardBroadcastFn);

    // Only the event types the dashboard actually renders — not routed
    // through the allEventsPattern fan-out above, which is analytics/audit-log
    // only.
    new events.Rule(this, 'DashboardBroadcastRule', {
      eventBus,
      eventPattern: {
        detailType: ['incident.created', 'incident.updated', 'incident.resolved', 'alert.received'],
      },
      targets: [new targets.SqsQueue(dashboard.queue)],
    });
  }
}

function kebab(pascalCase: string): string {
  return pascalCase.replace(/([a-z0-9])([A-Z])/g, '$1-$2').toLowerCase();
}
