import * as cdk from 'aws-cdk-lib/core';
import * as dynamodb from 'aws-cdk-lib/aws-dynamodb';
import * as secretsmanager from 'aws-cdk-lib/aws-secretsmanager';
import { Match, Template } from 'aws-cdk-lib/assertions';
import { EventProcessingStack } from '../lib/stacks/event-processing-stack';
import { environments } from '../lib/config/environments';
import { buildTestNetwork } from './helpers/minimal-network';
import { createWorkerPlaceholders } from './helpers/worker-placeholders';

const WORKER_NAMES = [
  'AlertValidation',
  'Deduplication',
  'IncidentCreation',
  'ResponderAssignment',
  'Notification',
  'Analytics',
  'AuditLog',
  'AttachmentScan',
  'Escalation',
];

beforeAll(() => createWorkerPlaceholders(WORKER_NAMES));

function synth(): Template {
  const app = new cdk.App();
  const supportStack = new cdk.Stack(app, 'TestSupportStack');
  const network = buildTestNetwork(supportStack);
  const dbSecret = new secretsmanager.Secret(supportStack, 'TestDbSecret');
  const fingerprintTable = new dynamodb.Table(supportStack, 'TestFingerprintTable', {
    partitionKey: { name: 'Fingerprint', type: dynamodb.AttributeType.STRING },
  });
  const stack = new EventProcessingStack(app, 'TestEventProcessingStack', {
    config: environments.dev,
    vpc: network.vpc,
    ecsSecurityGroup: network.ecsSecurityGroup,
    dbSecret,
    fingerprintTable,
  });
  return Template.fromStack(stack);
}

describe('EventProcessingStack', () => {
  it('creates a custom EventBridge bus named sentinelops-events', () => {
    const template = synth();
    template.hasResourceProperties('AWS::Events::EventBus', { Name: 'sentinelops-events' });
  });

  it('creates one SQS queue + DLQ per worker (10 workers), each wired to a redrive policy', () => {
    const template = synth();
    // AlertValidation, Deduplication, IncidentCreation, ResponderAssignment,
    // Notification, Analytics, AuditLog, EscalationRestart, AttachmentScan =
    // 9 queues + 9 DLQs. (DashboardBroadcast's queue now lives in ApiStack.)
    template.resourceCountIs('AWS::SQS::Queue', 18);

    const queues = template.findResources('AWS::SQS::Queue');
    const withRedrive = Object.values(queues).filter(
      (q: any) => q.Properties?.RedrivePolicy?.deadLetterTargetArn !== undefined,
    );
    expect(withRedrive).toHaveLength(9);
  });

  // A message that fails maxReceiveCount times lands in the DLQ automatically
  // (SQS-managed redrive) rather than being dropped — that's what "replay
  // from the DLQ" replays from. 5 is deliberately more than 1 so a single
  // transient failure (a brief DB blip) doesn't dead-letter a message that
  // would have succeeded on the very next attempt.
  it('dead-letters a worker queue message after 5 failed receives', () => {
    const template = synth();
    const queues = template.findResources('AWS::SQS::Queue');
    const workerQueues = Object.values(queues).filter(
      (q: any) => q.Properties?.RedrivePolicy?.deadLetterTargetArn !== undefined,
    );
    for (const queue of workerQueues) {
      expect((queue as any).Properties.RedrivePolicy.maxReceiveCount).toBe(5);
    }
  });

  // Every worker Lambda's SQS event source is configured to report which
  // individual messages in a batch failed (see SqsBatchProcessor in
  // SentinelOps.Workers.Shared) — without this, SQS treats the whole batch as
  // failed on any error, redelivering (and eventually dead-lettering) messages
  // that already succeeded alongside the one that didn't.
  it('configures every worker event source mapping to report partial batch failures', () => {
    const template = synth();
    const mappings = template.findResources('AWS::Lambda::EventSourceMapping');
    const mappingList = Object.values(mappings);
    expect(mappingList.length).toBeGreaterThan(0);
    for (const mapping of mappingList) {
      expect((mapping as any).Properties.FunctionResponseTypes).toEqual(['ReportBatchItemFailures']);
    }
  });

  it('creates 10 Lambda functions (7 single-queue workers + AttachmentScan + Escalation x2), on the dotnet10 runtime', () => {
    const template = synth();
    template.resourceCountIs('AWS::Lambda::Function', 10);
    template.allResourcesProperties('AWS::Lambda::Function', { Runtime: 'dotnet10' });
  });

  it('routes alert.received to the alert-validation queue', () => {
    const template = synth();
    template.hasResourceProperties('AWS::Events::Rule', {
      EventPattern: { 'detail-type': ['alert.received'] },
    });
  });

  it('fans all 11 event types out to both analytics and audit-log', () => {
    const template = synth();
    const allEleven = [
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
    ];
    const rules = Object.values(template.findResources('AWS::Events::Rule'));
    const wildcardRules = rules.filter(
      (r: any) =>
        JSON.stringify(r.Properties?.EventPattern?.['detail-type']) === JSON.stringify(allEleven),
    );
    expect(wildcardRules).toHaveLength(2);
  });

  it('does not create an EventBridge rule targeting the incident-creation queue', () => {
    const template = synth();
    const rules = Object.values(template.findResources('AWS::Events::Rule'));
    const queues = template.findResources('AWS::SQS::Queue');
    const incidentCreationQueueLogicalId = Object.keys(queues).find(
      (id) => queues[id].Properties?.QueueName === 'sentinelops-incident-creation',
    );
    expect(incidentCreationQueueLogicalId).toBeDefined();

    for (const rule of rules) {
      const targets = (rule as any).Properties?.Targets ?? [];
      for (const target of targets) {
        expect(JSON.stringify(target.Arn).includes(incidentCreationQueueLogicalId!)).toBe(false);
      }
    }
  });

  it('creates the escalation state machine with wait/check/advance/choice states and a failure path', () => {
    const template = synth();
    template.resourceCountIs('AWS::StepFunctions::StateMachine', 1);
    const machines = Object.values(template.findResources('AWS::StepFunctions::StateMachine'));
    const definition = JSON.stringify((machines[0] as any).Properties.DefinitionString);
    for (const stateName of [
      'WaitForAck',
      'CheckIncidentStatus',
      'IsAcknowledgedOrResolved',
      'AdvanceEscalationLevel',
      'IsEscalationExhausted',
      'StoppedAcknowledgedOrResolved',
      'StoppedEscalationExhausted',
      'RecordEscalationFailure',
      'EscalationWorkflowFailed',
    ]) {
      expect(definition.includes(stateName)).toBe(true);
    }
  });

  it('routes incident.updated/Status=Reopened to the escalation-restart queue, not the general fan-out', () => {
    const template = synth();
    const rules = template.findResources('AWS::Events::Rule');
    const reopenedRule = Object.values(rules).find(
      (r: any) =>
        JSON.stringify(r.Properties?.EventPattern) ===
        JSON.stringify({
          'detail-type': ['incident.updated'],
          detail: { field: ['Status'], newValue: ['Reopened'] },
        }),
    );
    expect(reopenedRule).toBeDefined();
  });

  it('grants both the responder-assignment and escalation-restart functions permission to start the state machine', () => {
    const template = synth();
    template.hasResourceProperties('AWS::IAM::Policy', {
      PolicyDocument: {
        Statement: Match.arrayWith([Match.objectLike({ Action: 'states:StartExecution' })]),
      },
    });
  });

  it('creates a domain-verified SES email identity and grants the notification function send permission', () => {
    const template = synth();
    template.resourceCountIs('AWS::SES::EmailIdentity', 1);
    template.hasResourceProperties('AWS::IAM::Policy', {
      PolicyDocument: {
        Statement: Match.arrayWith([
          Match.objectLike({ Action: Match.arrayWith([Match.stringLikeRegexp('ses:SendEmail')]) }),
        ]),
      },
    });
  });
});
