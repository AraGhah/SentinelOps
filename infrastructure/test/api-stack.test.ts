import * as cdk from 'aws-cdk-lib/core';
import * as cognito from 'aws-cdk-lib/aws-cognito';
import * as s3 from 'aws-cdk-lib/aws-s3';
import * as dynamodb from 'aws-cdk-lib/aws-dynamodb';
import * as events from 'aws-cdk-lib/aws-events';
import * as kms from 'aws-cdk-lib/aws-kms';
import * as logs from 'aws-cdk-lib/aws-logs';
import * as secretsmanager from 'aws-cdk-lib/aws-secretsmanager';
import { Match, Template } from 'aws-cdk-lib/assertions';
import { ApiStack } from '../lib/stacks/api-stack';
import { environments } from '../lib/config/environments';
import { buildTestNetwork } from './helpers/minimal-network';
import { createWorkerPlaceholders } from './helpers/worker-placeholders';

beforeAll(() => createWorkerPlaceholders(['Dashboard']));

function synth(): Template {
  const app = new cdk.App();
  const supportStack = new cdk.Stack(app, 'TestSupportStack');
  const network = buildTestNetwork(supportStack);
  const dbSecret = new secretsmanager.Secret(supportStack, 'TestDbSecret');
  const userPool = new cognito.UserPool(supportStack, 'TestUserPool');
  const userPoolClient = new cognito.UserPoolClient(supportStack, 'TestUserPoolClient', {
    userPool,
  });
  const attachmentsBucket = new s3.Bucket(supportStack, 'TestAttachmentsBucket');
  const reportsBucket = new s3.Bucket(supportStack, 'TestReportsBucket');
  const connectionsTable = new dynamodb.Table(supportStack, 'TestConnectionsTable', {
    partitionKey: { name: 'ConnectionId', type: dynamodb.AttributeType.STRING },
  });
  const eventBus = new events.EventBus(supportStack, 'TestEventBus');
  const dataKey = new kms.Key(supportStack, 'TestDataKey');
  const apiLogGroup = new logs.LogGroup(supportStack, 'TestApiLogGroup');

  const stack = new ApiStack(app, 'TestApiStack', {
    config: environments.dev,
    vpc: network.vpc,
    albSecurityGroup: network.albSecurityGroup,
    vpcLinkSecurityGroup: network.vpcLinkSecurityGroup,
    ecsSecurityGroup: network.ecsSecurityGroup,
    dbSecret,
    userPool,
    userPoolClient,
    attachmentsBucket,
    reportsBucket,
    connectionsTable,
    eventBus,
    dataKey,
    apiLogGroup,
  });
  return Template.fromStack(stack);
}

describe('ApiStack', () => {
  it('runs apps/api on ECS Fargate with its own task and execution roles, behind a private ALB', () => {
    const template = synth();
    template.resourceCountIs('AWS::ECS::Cluster', 1);
    template.hasResourceProperties('AWS::ECS::TaskDefinition', {
      RequiresCompatibilities: ['FARGATE'],
    });

    // No longer internet-facing — API Gateway + VpcLink is now the public
    // entry point (see the HTTP API assertions below).
    template.hasResourceProperties('AWS::ElasticLoadBalancingV2::LoadBalancer', {
      Scheme: 'internal',
    });

    const roles = Object.values(template.findResources('AWS::IAM::Role'));
    const ecsRoles = roles.filter((r: any) =>
      JSON.stringify(r.Properties?.AssumeRolePolicyDocument).includes('ecs-tasks.amazonaws.com'),
    );
    expect(ecsRoles.length).toBeGreaterThanOrEqual(2);
  });

  it('configures graceful shutdown (container stopTimeout, ALB deregistration delay, health-check grace period)', () => {
    const template = synth();
    const containers = Object.values(template.findResources('AWS::ECS::TaskDefinition'))[0] as any;
    expect(containers.Properties.ContainerDefinitions[0].StopTimeout).toBe(30);

    template.hasResourceProperties('AWS::ElasticLoadBalancingV2::TargetGroup', {
      TargetGroupAttributes: Match.arrayWith([
        Match.objectLike({ Key: 'deregistration_delay.timeout_seconds', Value: '20' }),
      ]),
    });

    template.hasResourceProperties('AWS::ECS::Service', { HealthCheckGracePeriodSeconds: 60 });
  });

  it('scales the API service out on CPU utilization, bounded by config min/max capacity', () => {
    const template = synth();
    template.hasResourceProperties('AWS::ApplicationAutoScaling::ScalableTarget', {
      ServiceNamespace: 'ecs',
      MinCapacity: environments.dev.ecs.apiDesiredCount,
      MaxCapacity: environments.dev.ecs.apiMaxCapacity,
    });
    template.hasResourceProperties('AWS::ApplicationAutoScaling::ScalingPolicy', {
      PolicyType: 'TargetTrackingScaling',
      TargetTrackingScalingPolicyConfiguration: Match.objectLike({
        TargetValue: 60,
        PredefinedMetricSpecification: { PredefinedMetricType: 'ECSServiceAverageCPUUtilization' },
      }),
    });
  });

  it('creates an HTTP API with a VpcLink integration in front of the private ALB', () => {
    const template = synth();
    template.resourceCountIs('AWS::ApiGatewayV2::VpcLink', 1);
    const apis = Object.values(template.findResources('AWS::ApiGatewayV2::Api'));
    const httpApi = apis.find((a: any) => a.Properties?.ProtocolType === 'HTTP');
    expect(httpApi).toBeDefined();
  });

  it('associates a WAFv2 web ACL (with a rate-based rule) to the HTTP API stage, not an ALB', () => {
    const template = synth();
    template.resourceCountIs('AWS::WAFv2::WebACLAssociation', 1);
    const association = Object.values(
      template.findResources('AWS::WAFv2::WebACLAssociation'),
    )[0] as any;
    expect(JSON.stringify(association.Properties.ResourceArn)).toContain('/stages/');

    const webAcls = Object.values(template.findResources('AWS::WAFv2::WebACL'));
    expect(webAcls).toHaveLength(1);
    const rules = (webAcls[0] as any).Properties.Rules;
    expect(rules.some((r: any) => r.Statement?.RateBasedStatement !== undefined)).toBe(true);
  });

  it('provisions a regional ACM certificate via DNS validation against an imported hosted zone', () => {
    const template = synth();
    template.resourceCountIs('AWS::CertificateManager::Certificate', 1);
    template.hasResourceProperties('AWS::CertificateManager::Certificate', {
      ValidationMethod: 'DNS',
    });
  });

  it('creates the dashboard WebSocket API with $connect/$disconnect routes', () => {
    const template = synth();
    template.resourceCountIs('AWS::ApiGatewayV2::Api', 2); // HTTP API + WebSocket API
    const apis = Object.values(template.findResources('AWS::ApiGatewayV2::Api'));
    expect(apis.some((a: any) => a.Properties?.ProtocolType === 'WEBSOCKET')).toBe(true);

    const routes = Object.values(template.findResources('AWS::ApiGatewayV2::Route'));
    const routeKeys = routes.map((r: any) => r.Properties?.RouteKey);
    expect(routeKeys).toEqual(expect.arrayContaining(['$connect', '$disconnect']));
  });

  it('creates 3 dashboard Lambda functions and their own broadcast queue', () => {
    const template = synth();
    template.resourceCountIs('AWS::Lambda::Function', 3);
    template.hasResourceProperties('AWS::SQS::Queue', {
      QueueName: 'sentinelops-dashboard-broadcast',
    });
  });

  it('grants the broadcast function permission to manage WebSocket connections', () => {
    const template = synth();
    template.hasResourceProperties('AWS::IAM::Policy', {
      PolicyDocument: {
        Statement: Match.arrayWith([Match.objectLike({ Action: 'execute-api:ManageConnections' })]),
      },
    });
  });
});
