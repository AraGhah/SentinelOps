import * as cdk from 'aws-cdk-lib/core';
import * as kms from 'aws-cdk-lib/aws-kms';
import { Template } from 'aws-cdk-lib/assertions';
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
});
