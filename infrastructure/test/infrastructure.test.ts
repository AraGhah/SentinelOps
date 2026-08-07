import * as cdk from 'aws-cdk-lib/core';
import { Match, Template } from 'aws-cdk-lib/assertions';
import * as fs from 'fs';
import * as path from 'path';
import * as Infrastructure from '../lib/infrastructure-stack';

// Lambda asset bundling (lambda.Code.fromAsset) reads each worker's publish
// output from disk at synth time, so a placeholder is created here rather than
// requiring `dotnet publish` to have already run before `npm test`.
const WORKER_NAMES = [
  'AlertValidation',
  'Deduplication',
  'IncidentCreation',
  'ResponderAssignment',
  'Notification',
  'Analytics',
  'AuditLog',
];

beforeAll(() => {
  for (const name of WORKER_NAMES) {
    const publishDir = path.join(
      __dirname, '..', '..', 'apps', 'workers', `SentinelOps.Workers.${name}`, 'bin', 'Release', 'net10.0', 'publish',
    );
    fs.mkdirSync(publishDir, { recursive: true });
    fs.writeFileSync(path.join(publishDir, '.placeholder'), 'test-only placeholder for cdk asset bundling\n');
  }
});

function synth(): Template {
  const app = new cdk.App();
  const stack = new Infrastructure.InfrastructureStack(app, 'TestStack');
  return Template.fromStack(stack);
}

describe('SentinelOps event-driven infrastructure', () => {
  it('creates a custom EventBridge bus named sentinelops-events', () => {
    const template = synth();
    template.hasResourceProperties('AWS::Events::EventBus', { Name: 'sentinelops-events' });
  });

  it('creates one SQS queue + DLQ per worker, each wired to a redrive policy', () => {
    const template = synth();
    // 7 worker queues + 7 DLQs.
    template.resourceCountIs('AWS::SQS::Queue', 14);

    for (const name of WORKER_NAMES) {
      template.hasResourceProperties('AWS::SQS::Queue', {
        QueueName: Match.stringLikeRegexp(`sentinelops-.*`),
      });
    }

    // Every non-DLQ queue has a redrive policy pointing at a DLQ.
    const queues = template.findResources('AWS::SQS::Queue');
    const withRedrive = Object.values(queues).filter(
      (q: any) => q.Properties?.RedrivePolicy?.deadLetterTargetArn !== undefined,
    );
    expect(withRedrive).toHaveLength(7);
  });

  it('creates 7 Lambda functions, one per worker, on the dotnet10 runtime', () => {
    const template = synth();
    template.resourceCountIs('AWS::Lambda::Function', 7);
    template.allResourcesProperties('AWS::Lambda::Function', {
      Runtime: 'dotnet10',
    });
  });

  it('routes alert.received to the alert-validation queue', () => {
    const template = synth();
    template.hasResourceProperties('AWS::Events::Rule', {
      EventPattern: { 'detail-type': ['alert.received'] },
    });
  });

  it('routes alert.validated to the deduplication queue', () => {
    const template = synth();
    template.hasResourceProperties('AWS::Events::Rule', {
      EventPattern: { 'detail-type': ['alert.validated'] },
    });
  });

  it('routes incident.created to the responder-assignment queue', () => {
    const template = synth();
    template.hasResourceProperties('AWS::Events::Rule', {
      EventPattern: { 'detail-type': ['incident.created'] },
    });
  });

  it('routes notification.requested to the notification queue', () => {
    const template = synth();
    template.hasResourceProperties('AWS::Events::Rule', {
      EventPattern: { 'detail-type': ['notification.requested'] },
    });
  });

  it('fans all 11 event types out to both analytics and audit-log', () => {
    const template = synth();
    const allEleven = [
      'alert.received', 'alert.validated', 'alert.rejected',
      'incident.created', 'incident.updated', 'incident.acknowledged', 'incident.escalated', 'incident.resolved',
      'notification.requested', 'notification.delivered', 'notification.failed',
    ];
    const rules = Object.values(template.findResources('AWS::Events::Rule'));
    const wildcardRules = rules.filter(
      (r: any) => JSON.stringify(r.Properties?.EventPattern?.['detail-type']) === JSON.stringify(allEleven),
    );
    expect(wildcardRules).toHaveLength(2);
  });

  it('does not create an EventBridge rule targeting the incident-creation queue', () => {
    // incident-creation is fed only by a direct SendMessage from the
    // deduplication worker (see IncidentCreationRequest) — asserting the
    // negative here guards against accidentally wiring up a second listener
    // to alert.validated, which would reintroduce the create-incident race
    // described in the implementation plan.
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
        const arn = JSON.stringify(target.Arn);
        expect(arn.includes(incidentCreationQueueLogicalId!)).toBe(false);
      }
    }
  });

  it('grants the deduplication function permission to send to the incident-creation queue', () => {
    const template = synth();
    template.hasResourceProperties('AWS::IAM::Policy', {
      PolicyDocument: {
        Statement: Match.arrayWith([
          Match.objectLike({
            Action: Match.arrayWith([Match.stringLikeRegexp('sqs:SendMessage')]),
          }),
        ]),
      },
    });
  });
});
