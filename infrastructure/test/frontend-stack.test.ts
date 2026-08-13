import * as cdk from 'aws-cdk-lib/core';
import { Template } from 'aws-cdk-lib/assertions';
import { FrontendStack } from '../lib/stacks/frontend-stack';
import { environments } from '../lib/config/environments';
import { buildTestNetwork } from './helpers/minimal-network';

// FrontendStack's ACM certificate must be in us-east-1 for CloudFront — this
// test stack is pinned there too, matching bin/infrastructure.ts's real
// shared-env pattern.
function synth(): Template {
  const app = new cdk.App();
  const supportStack = new cdk.Stack(app, 'TestSupportStack', { env: { region: 'us-east-1' } });
  const network = buildTestNetwork(supportStack);
  const stack = new FrontendStack(app, 'TestFrontendStack', {
    config: environments.dev,
    env: { region: 'us-east-1' },
    vpc: network.vpc,
    apiUrl: 'https://api-dev.sentinelops.example',
  });
  return Template.fromStack(stack);
}

describe('FrontendStack', () => {
  it('runs apps/web on ECS Fargate behind a public ALB', () => {
    const template = synth();
    template.resourceCountIs('AWS::ECS::Cluster', 1);
    template.hasResourceProperties('AWS::ECS::TaskDefinition', {
      RequiresCompatibilities: ['FARGATE'],
    });
    template.hasResourceProperties('AWS::ElasticLoadBalancingV2::LoadBalancer', {
      Scheme: 'internet-facing',
    });
  });

  it('rejects requests without the X-Origin-Verify header at the ALB listener', () => {
    const template = synth();
    const rules = Object.values(
      template.findResources('AWS::ElasticLoadBalancingV2::ListenerRule'),
    );
    const originVerifyRule = rules.find((r: any) =>
      JSON.stringify(r.Properties?.Conditions).includes('X-Origin-Verify'),
    );
    expect(originVerifyRule).toBeDefined();

    const listeners = Object.values(
      template.findResources('AWS::ElasticLoadBalancingV2::Listener'),
    );
    const httpsListener = listeners.find((l: any) => l.Properties?.Port === 443);
    expect((httpsListener as any).Properties.DefaultActions[0].FixedResponseConfig.StatusCode).toBe(
      '403',
    );
  });

  it('creates a CloudFront distribution that sends the same X-Origin-Verify header to its origin', () => {
    const template = synth();
    template.resourceCountIs('AWS::CloudFront::Distribution', 1);
    const distributions = Object.values(template.findResources('AWS::CloudFront::Distribution'));
    const config = JSON.stringify((distributions[0] as any).Properties.DistributionConfig);
    expect(config).toContain('X-Origin-Verify');
  });

  it('provisions its ACM certificate via DNS validation (co-located in us-east-1 with everything else)', () => {
    const template = synth();
    template.resourceCountIs('AWS::CertificateManager::Certificate', 1);
    template.hasResourceProperties('AWS::CertificateManager::Certificate', {
      ValidationMethod: 'DNS',
    });
  });

  it('creates a Route53 alias record targeting the CloudFront distribution', () => {
    const template = synth();
    template.hasResourceProperties('AWS::Route53::RecordSet', { Type: 'A' });
  });
});
