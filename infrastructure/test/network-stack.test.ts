import * as cdk from 'aws-cdk-lib/core';
import { Template } from 'aws-cdk-lib/assertions';
import { NetworkStack } from '../lib/stacks/network-stack';
import { environments } from '../lib/config/environments';

function synth(): Template {
  const app = new cdk.App();
  const stack = new NetworkStack(app, 'TestNetworkStack', { config: environments.dev });
  return Template.fromStack(stack);
}

describe('NetworkStack', () => {
  it('creates a VPC with public, private-with-egress, and isolated subnet tiers, and only one NAT gateway', () => {
    const template = synth();
    // 2 AZs x 3 subnet groups (Public, PrivateApp, PrivateDb).
    template.resourceCountIs('AWS::EC2::Subnet', 6);
    template.resourceCountIs('AWS::EC2::NatGateway', 1);

    // Only PrivateApp's 2 AZ-subnets get a default route through the NAT
    // gateway — PrivateDb (Aurora's tier) gets none, which is what makes it
    // PRIVATE_ISOLATED rather than merely "private."
    const routes = Object.values(template.findResources('AWS::EC2::Route'));
    const natRoutes = routes.filter((r: any) => r.Properties?.NatGatewayId !== undefined);
    expect(natRoutes).toHaveLength(2);
  });

  it('creates 3 security groups (ALB, ECS, VpcLink — RDS/rotation live in DatabaseStack) and does not grant any of them unrestricted (0.0.0.0/0) egress', () => {
    const template = synth();
    const groups = Object.values(template.findResources('AWS::EC2::SecurityGroup'));
    expect(groups).toHaveLength(3);
    for (const group of groups) {
      const egress = (group as any).Properties?.SecurityGroupEgress ?? [];
      for (const rule of egress) {
        const isWideOpen =
          rule.CidrIp === '0.0.0.0/0' && (rule.IpProtocol === '-1' || rule.FromPort === undefined);
        expect(isWideOpen).toBe(false);
      }
    }
  });

  it('gives the API ALB security group no public ingress — it is now private, reachable only via the VPC Link', () => {
    const template = synth();
    const groups = Object.values(template.findResources('AWS::EC2::SecurityGroup'));
    const albSg = groups.find((g: any) =>
      (g.Properties?.GroupDescription ?? '').includes('SentinelOps API ALB'),
    );
    expect(albSg).toBeDefined();
    const ingress = (albSg as any).Properties?.SecurityGroupIngress ?? [];
    for (const rule of ingress) {
      expect(rule.CidrIp).not.toBe('0.0.0.0/0');
    }
  });
});
