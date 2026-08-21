import * as cdk from 'aws-cdk-lib/core';
import * as kms from 'aws-cdk-lib/aws-kms';
import { Match, Template } from 'aws-cdk-lib/assertions';
import { ObservabilityStack } from '../lib/stacks/observability-stack';
import { environments } from '../lib/config/environments';

function synth(): Template {
  const app = new cdk.App();
  const keyStack = new cdk.Stack(app, 'TestKeyStack');
  const dataKey = new kms.Key(keyStack, 'TestDataKey');
  const stack = new ObservabilityStack(app, 'TestObservabilityStack', {
    config: environments.dev,
    dataKey,
  });
  return Template.fromStack(stack);
}

describe('ObservabilityStack', () => {
  it('enables a multi-region CloudTrail trail with CloudWatch Logs delivery', () => {
    const template = synth();
    template.resourceCountIs('AWS::CloudTrail::Trail', 1);
    template.hasResourceProperties('AWS::CloudTrail::Trail', {
      IsMultiRegionTrail: true,
      IncludeGlobalServiceEvents: true,
    });
  });

  it('blocks public access on the CloudTrail bucket', () => {
    const template = synth();
    template.hasResourceProperties('AWS::S3::Bucket', {
      PublicAccessBlockConfiguration: {
        BlockPublicAcls: true,
        BlockPublicPolicy: true,
        IgnorePublicAcls: true,
        RestrictPublicBuckets: true,
      },
    });
  });

  it('creates the API log group and a CloudWatch dashboard', () => {
    const template = synth();
    template.hasResourceProperties('AWS::Logs::LogGroup', { LogGroupName: '/sentinelops/api' });
    template.resourceCountIs('AWS::CloudWatch::Dashboard', 1);
  });

  it('creates a monthly cost budget scoped to this environment, notifying the alarm topic at 80% actual and 100% forecasted', () => {
    const template = synth();
    template.resourceCountIs('AWS::Budgets::Budget', 1);
    template.hasResourceProperties('AWS::Budgets::Budget', {
      Budget: {
        BudgetType: 'COST',
        TimeUnit: 'MONTHLY',
        BudgetLimit: { Amount: environments.dev.monthlyBudgetUsd, Unit: 'USD' },
        CostFilters: { TagKeyValue: ['user:Environment$dev'] },
      },
      NotificationsWithSubscribers: [
        {
          Notification: { NotificationType: 'ACTUAL', Threshold: 80 },
          Subscribers: [{ SubscriptionType: 'SNS' }],
        },
        {
          Notification: { NotificationType: 'FORECASTED', Threshold: 100 },
          Subscribers: [{ SubscriptionType: 'SNS' }],
        },
      ],
    });
  });

  it('alarms on the dashboard-broadcast queue DLQ and oldest-message age', () => {
    const template = synth();
    template.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: 'sentinelops-dev-dashboard-broadcast-dlq-not-empty',
    });
    template.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: 'sentinelops-dev-dashboard-broadcast-oldest-message-too-old',
    });
  });

  it('grants AWS Budgets permission to publish to the alarm topic', () => {
    const template = synth();
    template.hasResourceProperties('AWS::SNS::TopicPolicy', {
      PolicyDocument: {
        Statement: Match.arrayWith([
          Match.objectLike({
            Effect: 'Allow',
            Action: 'sns:Publish',
            Principal: { Service: 'budgets.amazonaws.com' },
          }),
        ]),
      },
    });
  });
});
