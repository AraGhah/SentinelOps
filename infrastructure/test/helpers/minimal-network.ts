import * as cdk from 'aws-cdk-lib/core';
import * as ec2 from 'aws-cdk-lib/aws-ec2';

export interface TestNetwork {
  vpc: ec2.Vpc;
  albSecurityGroup: ec2.SecurityGroup;
  ecsSecurityGroup: ec2.SecurityGroup;
  vpcLinkSecurityGroup: ec2.SecurityGroup;
}

// Throwaway VPC + security groups for stacks tested in isolation. Mirrors
// NetworkStack's 3 security groups; DatabaseStack owns its RDS/rotation security
// groups itself, so tests exercising it build those directly instead.
export function buildTestNetwork(stack: cdk.Stack): TestNetwork {
  const vpc = new ec2.Vpc(stack, 'TestVpc', {
    maxAzs: 2,
    natGateways: 1,
    subnetConfiguration: [
      { name: 'Public', subnetType: ec2.SubnetType.PUBLIC, cidrMask: 24 },
      { name: 'PrivateApp', subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS, cidrMask: 24 },
      { name: 'PrivateDb', subnetType: ec2.SubnetType.PRIVATE_ISOLATED, cidrMask: 24 },
    ],
  });
  return {
    vpc,
    albSecurityGroup: new ec2.SecurityGroup(stack, 'TestAlbSg', { vpc }),
    ecsSecurityGroup: new ec2.SecurityGroup(stack, 'TestEcsSg', { vpc }),
    vpcLinkSecurityGroup: new ec2.SecurityGroup(stack, 'TestVpcLinkSg', { vpc }),
  };
}
