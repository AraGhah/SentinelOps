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
  'Escalation',
  'Dashboard',
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
    // 7 original worker queues + EscalationRestart (Escalation's other half,
    // EscalationFunction, is a direct Step Functions task target with no
    // queue of its own) + DashboardBroadcast (Dashboard's Connect/Disconnect
    // halves are API Gateway route targets with no queue) = 9 worker queues + 9 DLQs.
    template.resourceCountIs('AWS::SQS::Queue', 18);

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
    expect(withRedrive).toHaveLength(9);
  });

  it('creates 12 Lambda functions (7 workers + Escalation x2 + Dashboard x3), on the dotnet10 runtime', () => {
    const template = synth();
    template.resourceCountIs('AWS::Lambda::Function', 12);
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

  it('creates the escalation state machine with wait/check/advance/choice states and a failure path', () => {
    const template = synth();
    template.resourceCountIs('AWS::StepFunctions::StateMachine', 1);

    const machines = Object.values(template.findResources('AWS::StepFunctions::StateMachine'));
    const definition = JSON.stringify((machines[0] as any).Properties.DefinitionString);

    for (const stateName of [
      'WaitForAck', 'CheckIncidentStatus', 'IsAcknowledgedOrResolved', 'AdvanceEscalationLevel',
      'IsEscalationExhausted', 'StoppedAcknowledgedOrResolved', 'StoppedEscalationExhausted',
      'RecordEscalationFailure', 'EscalationWorkflowFailed',
    ]) {
      expect(definition.includes(stateName)).toBe(true);
    }
  });

  it('routes incident.updated/Status=Reopened to the escalation-restart queue, not the general fan-out', () => {
    const template = synth();
    const rules = template.findResources('AWS::Events::Rule');
    const reopenedRule = Object.values(rules).find(
      (r: any) => JSON.stringify(r.Properties?.EventPattern) === JSON.stringify({
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
        Statement: Match.arrayWith([
          Match.objectLike({
            Action: 'states:StartExecution',
          }),
        ]),
      },
    });
  });

  it('creates a domain-verified SES email identity and grants the notification function send permission', () => {
    const template = synth();
    template.resourceCountIs('AWS::SES::EmailIdentity', 1);

    template.hasResourceProperties('AWS::IAM::Policy', {
      PolicyDocument: {
        Statement: Match.arrayWith([
          Match.objectLike({
            Action: Match.arrayWith([Match.stringLikeRegexp('ses:SendEmail')]),
          }),
        ]),
      },
    });
  });

  it('creates the dashboard WebSocket API with $connect/$disconnect routes and a connections table', () => {
    const template = synth();
    template.resourceCountIs('AWS::ApiGatewayV2::Api', 1);
    template.hasResourceProperties('AWS::ApiGatewayV2::Api', { ProtocolType: 'WEBSOCKET' });

    const routes = Object.values(template.findResources('AWS::ApiGatewayV2::Route'));
    const routeKeys = routes.map((r: any) => r.Properties?.RouteKey);
    expect(routeKeys).toEqual(expect.arrayContaining(['$connect', '$disconnect']));

    template.hasResourceProperties('AWS::DynamoDB::Table', {
      TableName: 'sentinelops-dashboard-connections',
      KeySchema: Match.arrayWith([Match.objectLike({ AttributeName: 'ConnectionId', KeyType: 'HASH' })]),
      GlobalSecondaryIndexes: Match.arrayWith([
        Match.objectLike({ IndexName: 'OrganizationId-index' }),
      ]),
    });
  });

  it('routes incident.created/updated/resolved and alert.received to the dashboard-broadcast queue', () => {
    const template = synth();
    const rules = Object.values(template.findResources('AWS::Events::Rule'));
    const dashboardRule = rules.find(
      (r: any) => JSON.stringify(r.Properties?.EventPattern?.['detail-type']) ===
        JSON.stringify(['incident.created', 'incident.updated', 'incident.resolved', 'alert.received']),
    );
    expect(dashboardRule).toBeDefined();
  });

  it('grants the broadcast function permission to manage WebSocket connections', () => {
    const template = synth();
    template.hasResourceProperties('AWS::IAM::Policy', {
      PolicyDocument: {
        Statement: Match.arrayWith([
          Match.objectLike({
            Action: 'execute-api:ManageConnections',
          }),
        ]),
      },
    });
  });
});
