import * as cdk from 'aws-cdk-lib/core';
import * as ec2 from 'aws-cdk-lib/aws-ec2';
import * as sqs from 'aws-cdk-lib/aws-sqs';
import * as dynamodb from 'aws-cdk-lib/aws-dynamodb';
import * as ses from 'aws-cdk-lib/aws-ses';
import * as events from 'aws-cdk-lib/aws-events';
import * as targets from 'aws-cdk-lib/aws-events-targets';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import { SqsEventSource } from 'aws-cdk-lib/aws-lambda-event-sources';
import * as sfn from 'aws-cdk-lib/aws-stepfunctions';
import * as tasks from 'aws-cdk-lib/aws-stepfunctions-tasks';
import * as secretsmanager from 'aws-cdk-lib/aws-secretsmanager';
import { Construct } from 'constructs';
import * as path from 'path';
import { EnvironmentConfig } from '../config/environments';

interface WorkerQueue {
  queue: sqs.Queue;
  dlq: sqs.Queue;
}

// Publish path for `dotnet publish` for each worker project — not built by
// this stack; run `dotnet publish -c Release` in the worker's directory
// before `cdk deploy`.
const WORKERS_DIR = path.join(__dirname, '..', '..', '..', 'apps', 'workers');

export interface EventProcessingStackProps extends cdk.StackProps {
  config: EnvironmentConfig;
  vpc: ec2.IVpc;
  ecsSecurityGroup: ec2.ISecurityGroup;
  dbSecret: secretsmanager.ISecret;
  fingerprintTable: dynamodb.ITable;
}

export class EventProcessingStack extends cdk.Stack {
  public readonly eventBus: events.EventBus;
  public readonly escalationStateMachine: sfn.StateMachine;

  constructor(scope: Construct, id: string, props: EventProcessingStackProps) {
    super(scope, id, props);

    const { vpc, ecsSecurityGroup, dbSecret, fingerprintTable } = props;

    // --- Event-driven backbone -------------------------------------------
    // Custom bus (not the default bus) so alert/incident/notification events
    // are isolated from anything else running in this account, and so rules
    // here can safely use wildcard source/detail-type matches (analytics,
    // audit-log) without catching unrelated traffic.
    this.eventBus = new events.EventBus(this, 'SentinelOpsEventBus', {
      eventBusName: 'sentinelops-events',
    });

    new cdk.CfnOutput(this, 'EventBusName', {
      value: this.eventBus.eventBusName,
      description: 'EventBridge bus name — set as Aws__EventBridge__EventBusName on apps/api',
    });
    new cdk.CfnOutput(this, 'EventBusArn', { value: this.eventBus.eventBusArn });

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

    // --- Notification email -------------------------------------------------
    // Domain verification via SES's own DKIM records rather than a
    // Route53-hosted-zone identity — the three CNAME outputs below have to be
    // added to the domain's DNS manually before SES will accept sends from it.
    const notificationDomainName = new cdk.CfnParameter(this, 'NotificationDomainName', {
      type: 'String',
      default: props.config.notificationDomainNameDefault,
      description:
        'Domain SES sends incident emails from (e.g. alerts.example.com). Add the three ' +
        "EmailIdentityDkimRecord CNAME outputs to this domain's DNS to complete verification.",
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

    // Every worker resolves its Postgres connection string at cold start by
    // reading this secret's ARN and calling Secrets Manager directly (see
    // DbConnectionStringResolver in SentinelOps.Workers.Shared). A rotated
    // secret takes effect on each function's next cold start (see
    // docs/security/security-assumptions.md for the propagation-lag tradeoff).
    const workerFunction = (
      name: string,
      projectDirName: string,
      queue: sqs.Queue,
      opts: {
        memoryMb?: number;
        timeoutSeconds?: number;
        reservedConcurrency?: number;
        extraEnv?: Record<string, string>;
      } = {},
    ) => {
      const fn = new lambda.Function(this, `${name}Function`, {
        functionName: `sentinelops-${kebab(name)}`,
        runtime: lambda.Runtime.DOTNET_10,
        architecture: lambda.Architecture.X86_64,
        handler: `SentinelOps.Workers.${name}::SentinelOps.Workers.${name}.Function::FunctionHandler`,
        code: lambda.Code.fromAsset(
          path.join(WORKERS_DIR, projectDirName, 'bin', 'Release', 'net10.0', 'publish'),
        ),
        memorySize: opts.memoryMb ?? 512,
        timeout: cdk.Duration.seconds(opts.timeoutSeconds ?? 30),
        // Conservative on purpose — every worker shares the same Aurora
        // cluster apps/api uses, so this caps how many concurrent connections
        // a burst of messages can open.
        reservedConcurrentExecutions: opts.reservedConcurrency ?? 5,
        vpc,
        vpcSubnets: { subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS },
        securityGroups: [ecsSecurityGroup],
        environment: {
          DB_SECRET_ARN: dbSecret.secretArn,
          EVENT_BUS_NAME: this.eventBus.eventBusName,
          ...opts.extraEnv,
        },
      });
      fn.addEventSource(
        new SqsEventSource(queue, { batchSize: 10, reportBatchItemFailures: true }),
      );
      this.eventBus.grantPutEventsTo(fn);
      dbSecret.grantRead(fn);
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
      eventBus: this.eventBus,
      eventPattern: { detailType: ['alert.received'] },
      targets: [new targets.SqsQueue(alertValidation.queue)],
    });
    new events.Rule(this, 'AlertValidatedRule', {
      eventBus: this.eventBus,
      eventPattern: { detailType: ['alert.validated'] },
      targets: [new targets.SqsQueue(deduplication.queue)],
    });
    // No rule targets incidentCreation.queue: it's fed only by a direct
    // SendMessage from the deduplication worker once an alert is confirmed
    // unique — routing that decision through EventBridge as a second
    // `alert.validated` listener would race with the deduplication worker's
    // own decision.
    new events.Rule(this, 'IncidentCreatedRule', {
      eventBus: this.eventBus,
      eventPattern: { detailType: ['incident.created'] },
      targets: [new targets.SqsQueue(responderAssignment.queue)],
    });
    new events.Rule(this, 'NotificationRequestedRule', {
      eventBus: this.eventBus,
      eventPattern: { detailType: ['notification.requested'] },
      targets: [new targets.SqsQueue(notification.queue)],
    });
    // Content-filtered on the specific field/value IncidentsController and
    // the deduplication worker both use for a reopen — not every
    // `incident.updated` event, just the one that means "escalation needs to
    // restart."
    new events.Rule(this, 'IncidentReopenedRule', {
      eventBus: this.eventBus,
      eventPattern: {
        detailType: ['incident.updated'],
        detail: { field: ['Status'], newValue: ['Reopened'] },
      },
      targets: [new targets.SqsQueue(escalationRestart.queue)],
    });
    // Fan-out to the two pure-observer workers: every one of the 11 event
    // types this system defines. CDK's typed EventPattern.detailType only
    // accepts literal strings, not a true wildcard/prefix matcher, so this is
    // spelled out explicitly — a 12th event type added later must be added
    // here too, deliberately.
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
      eventBus: this.eventBus,
      eventPattern: allEventsPattern,
      targets: [new targets.SqsQueue(analytics.queue)],
    });
    new events.Rule(this, 'AllEventsToAuditLogRule', {
      eventBus: this.eventBus,
      eventPattern: allEventsPattern,
      targets: [new targets.SqsQueue(auditLog.queue)],
    });

    workerFunction('AlertValidation', 'SentinelOps.Workers.AlertValidation', alertValidation.queue);

    const dedupFn = workerFunction(
      'Deduplication',
      'SentinelOps.Workers.Deduplication',
      deduplication.queue,
      {
        extraEnv: {
          INCIDENT_CREATION_QUEUE_URL: incidentCreation.queue.queueUrl,
          FINGERPRINT_TABLE_NAME: fingerprintTable.tableName,
        },
      },
    );
    incidentCreation.queue.grantSendMessages(dedupFn);
    fingerprintTable.grantReadWriteData(dedupFn);

    const incidentCreationFn = workerFunction(
      'IncidentCreation',
      'SentinelOps.Workers.IncidentCreation',
      incidentCreation.queue,
      {
        extraEnv: { FINGERPRINT_TABLE_NAME: fingerprintTable.tableName },
      },
    );
    fingerprintTable.grantReadWriteData(incidentCreationFn);
    const responderAssignmentFn = workerFunction(
      'ResponderAssignment',
      'SentinelOps.Workers.ResponderAssignment',
      responderAssignment.queue,
    );
    const notificationFn = workerFunction(
      'Notification',
      'SentinelOps.Workers.Notification',
      notification.queue,
      {
        memoryMb: 256,
        timeoutSeconds: 15,
        extraEnv: { SES_SENDER_EMAIL: senderEmail },
      },
    );
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

    // --- Attachment malware-scan result handling ------------------------------
    // GuardDuty findings land on the account's default bus, not the
    // sentinelops-events bus every other rule in this stack matches against —
    // constructed without `eventBus` so it targets the default one.
    const attachmentScan = workerQueue('AttachmentScan', 15);
    new events.Rule(this, 'AttachmentScanResultRule', {
      eventPattern: {
        source: ['aws.guardduty'],
        detailType: ['GuardDuty Malware Protection Object Scan Result'],
      },
      targets: [new targets.SqsQueue(attachmentScan.queue)],
    });
    workerFunction('AttachmentScan', 'SentinelOps.Workers.AttachmentScan', attachmentScan.queue, {
      memoryMb: 256,
      timeoutSeconds: 15,
      reservedConcurrency: 10,
    });

    // --- Escalation state machine -------------------------------------------
    // SentinelOps.Workers.Escalation publishes as one package but deploys as
    // two Lambda functions with different handlers: EscalationFunction (a
    // direct Step Functions task target — no SQS queue, request/response) and
    // RestartFunction (SQS-triggered, on a reopened incident). Neither fits
    // the `workerFunction` factory's "one queue, one Function class per
    // project" assumption, so both are built by hand here.
    const escalationCodeAsset = lambda.Code.fromAsset(
      path.join(
        WORKERS_DIR,
        'SentinelOps.Workers.Escalation',
        'bin',
        'Release',
        'net10.0',
        'publish',
      ),
    );

    const escalationFn = new lambda.Function(this, 'EscalationFunction', {
      functionName: 'sentinelops-escalation',
      runtime: lambda.Runtime.DOTNET_10,
      architecture: lambda.Architecture.X86_64,
      handler:
        'SentinelOps.Workers.Escalation::SentinelOps.Workers.Escalation.EscalationFunction::FunctionHandler',
      code: escalationCodeAsset,
      memorySize: 512,
      timeout: cdk.Duration.seconds(30),
      reservedConcurrentExecutions: 5,
      vpc,
      vpcSubnets: { subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS },
      securityGroups: [ecsSecurityGroup],
      environment: {
        DB_SECRET_ARN: dbSecret.secretArn,
        EVENT_BUS_NAME: this.eventBus.eventBusName,
      },
    });
    this.eventBus.grantPutEventsTo(escalationFn);
    dbSecret.grantRead(escalationFn);

    const escalationRestartFn = new lambda.Function(this, 'EscalationRestartFunction', {
      functionName: 'sentinelops-escalation-restart',
      runtime: lambda.Runtime.DOTNET_10,
      architecture: lambda.Architecture.X86_64,
      handler:
        'SentinelOps.Workers.Escalation::SentinelOps.Workers.Escalation.RestartFunction::FunctionHandler',
      code: escalationCodeAsset,
      memorySize: 512,
      timeout: cdk.Duration.seconds(30),
      reservedConcurrentExecutions: 5,
      vpc,
      vpcSubnets: { subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS },
      securityGroups: [ecsSecurityGroup],
      environment: {
        DB_SECRET_ARN: dbSecret.secretArn,
        EVENT_BUS_NAME: this.eventBus.eventBusName,
      },
    });
    escalationRestartFn.addEventSource(
      new SqsEventSource(escalationRestart.queue, { batchSize: 10, reportBatchItemFailures: true }),
    );
    this.eventBus.grantPutEventsTo(escalationRestartFn);
    dbSecret.grantRead(escalationRestartFn);

    // Every task both accepts and returns EscalationStateInput's shape —
    // `payloadResponseOnly` makes the Lambda's JSON return value replace the
    // whole state, so no Wait/Choice state here ever needs to reshape or
    // merge JSON paths.
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
        .when(
          sfn.Condition.booleanEquals('$.acknowledgedOrResolved', true),
          stoppedAcknowledgedOrResolved,
        )
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
    this.escalationStateMachine = new sfn.StateMachine(this, 'EscalationStateMachine', {
      stateMachineName: 'sentinelops-incident-escalation',
      definitionBody: sfn.DefinitionBody.fromChainable(waitForAck),
      timeout: cdk.Duration.days(7),
    });

    new cdk.CfnOutput(this, 'EscalationStateMachineArn', {
      value: this.escalationStateMachine.stateMachineArn,
    });

    this.escalationStateMachine.grantStartExecution(responderAssignmentFn);
    this.escalationStateMachine.grantStartExecution(escalationRestartFn);
    responderAssignmentFn.addEnvironment(
      'ESCALATION_STATE_MACHINE_ARN',
      this.escalationStateMachine.stateMachineArn,
    );
    escalationRestartFn.addEnvironment(
      'ESCALATION_STATE_MACHINE_ARN',
      this.escalationStateMachine.stateMachineArn,
    );
  }
}

function kebab(pascalCase: string): string {
  return pascalCase.replace(/([a-z0-9])([A-Z])/g, '$1-$2').toLowerCase();
}
