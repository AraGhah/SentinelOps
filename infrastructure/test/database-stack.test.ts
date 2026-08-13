import * as cdk from 'aws-cdk-lib/core';
import * as kms from 'aws-cdk-lib/aws-kms';
import { Template } from 'aws-cdk-lib/assertions';
import { DatabaseStack } from '../lib/stacks/database-stack';
import { environments } from '../lib/config/environments';
import { buildTestNetwork } from './helpers/minimal-network';

function synth(): Template {
  const app = new cdk.App();
  const netStack = new cdk.Stack(app, 'TestNetStack');
  const network = buildTestNetwork(netStack);
  const dataKey = new kms.Key(netStack, 'TestDataKey');
  const stack = new DatabaseStack(app, 'TestDatabaseStack', {
    config: environments.dev,
    vpc: network.vpc,
    ecsSecurityGroup: network.ecsSecurityGroup,
    dataKey,
  });
  return Template.fromStack(stack);
}

describe('DatabaseStack', () => {
  it('creates an Aurora PostgreSQL Serverless v2 cluster, encrypted at rest with the shared CMK', () => {
    const template = synth();
    template.resourceCountIs('AWS::RDS::DBCluster', 1);
    template.hasResourceProperties('AWS::RDS::DBCluster', {
      Engine: 'aurora-postgresql',
      StorageEncrypted: true,
      ServerlessV2ScalingConfiguration: { MinCapacity: 0.5, MaxCapacity: 2 },
    });
  });

  it('creates exactly one serverless v2 writer instance (no reader)', () => {
    const template = synth();
    template.resourceCountIs('AWS::RDS::DBInstance', 1);
    template.hasResourceProperties('AWS::RDS::DBInstance', { DBInstanceClass: 'db.serverless' });
  });

  it('rotates the Aurora credential secret automatically every 30 days', () => {
    const template = synth();
    template.resourceCountIs('AWS::SecretsManager::RotationSchedule', 1);
    template.hasResourceProperties('AWS::SecretsManager::RotationSchedule', {
      RotationRules: { ScheduleExpression: 'rate(30 days)' },
    });
  });

  it('forces SSL on the Postgres wire protocol via the parameter group', () => {
    const template = synth();
    template.hasResourceProperties('AWS::RDS::DBClusterParameterGroup', {
      Parameters: { 'rds.force_ssl': '1' },
    });
  });
});
