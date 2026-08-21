import * as cdk from 'aws-cdk-lib/core';
import { Match, Template } from 'aws-cdk-lib/assertions';
import { StorageStack } from '../lib/stacks/storage-stack';
import { environments } from '../lib/config/environments';

function synth(config = environments.dev): Template {
  const app = new cdk.App();
  const stack = new StorageStack(app, 'TestStorageStack', { config });
  return Template.fromStack(stack);
}

describe('StorageStack', () => {
  it('creates a rotation-enabled KMS CMK', () => {
    const template = synth();
    template.resourceCountIs('AWS::KMS::Key', 1);
    template.hasResourceProperties('AWS::KMS::Key', { EnableKeyRotation: true });
  });

  it('encrypts both S3 buckets with the shared KMS CMK and blocks public access', () => {
    const template = synth();
    const buckets = Object.values(template.findResources('AWS::S3::Bucket'));
    expect(buckets).toHaveLength(2);
    for (const bucket of buckets) {
      const props = (bucket as any).Properties;
      expect(
        props.BucketEncryption.ServerSideEncryptionConfiguration[0].ServerSideEncryptionByDefault
          .SSEAlgorithm,
      ).toBe('aws:kms');
      expect(props.PublicAccessBlockConfiguration).toEqual({
        BlockPublicAcls: true,
        BlockPublicPolicy: true,
        IgnorePublicAcls: true,
        RestrictPublicBuckets: true,
      });
    }
  });

  it('declares a GuardDuty Malware Protection Plan for the attachments bucket', () => {
    const template = synth();
    template.resourceCountIs('AWS::GuardDuty::MalwareProtectionPlan', 1);
  });

  it('creates the two DynamoDB tables with TTL enabled', () => {
    const template = synth();
    template.hasResourceProperties('AWS::DynamoDB::Table', {
      TableName: 'sentinelops-alert-fingerprints',
      TimeToLiveSpecification: { AttributeName: 'ExpiresAt', Enabled: true },
    });
    template.hasResourceProperties('AWS::DynamoDB::Table', {
      TableName: 'sentinelops-dashboard-connections',
      GlobalSecondaryIndexes: Match.arrayWith([
        Match.objectLike({ IndexName: 'OrganizationId-index' }),
      ]),
    });
  });

  it('enables point-in-time recovery on both DynamoDB tables', () => {
    const template = synth();
    template.hasResourceProperties('AWS::DynamoDB::Table', {
      TableName: 'sentinelops-alert-fingerprints',
      PointInTimeRecoverySpecification: { PointInTimeRecoveryEnabled: true },
    });
    template.hasResourceProperties('AWS::DynamoDB::Table', {
      TableName: 'sentinelops-dashboard-connections',
      PointInTimeRecoverySpecification: { PointInTimeRecoveryEnabled: true },
    });
  });

  it('uses DESTROY removal policy in dev and RETAIN in staging for data-bearing resources', () => {
    const devTemplate = synth(environments.dev);
    devTemplate.hasResource('AWS::DynamoDB::Table', { DeletionPolicy: 'Delete' });

    const stagingTemplate = synth(environments.staging);
    stagingTemplate.hasResource('AWS::DynamoDB::Table', { DeletionPolicy: 'Retain' });
  });
});
