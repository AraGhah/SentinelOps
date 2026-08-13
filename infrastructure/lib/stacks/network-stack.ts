import * as cdk from 'aws-cdk-lib/core';
import * as ec2 from 'aws-cdk-lib/aws-ec2';
import { Construct } from 'constructs';
import { EnvironmentConfig } from '../config/environments';

export interface NetworkStackProps extends cdk.StackProps {
  config: EnvironmentConfig;
}

export class NetworkStack extends cdk.Stack {
  public readonly vpc: ec2.Vpc;
  public readonly albSecurityGroup: ec2.SecurityGroup;
  public readonly ecsSecurityGroup: ec2.SecurityGroup;
  public readonly vpcLinkSecurityGroup: ec2.SecurityGroup;

  constructor(scope: Construct, id: string, props: NetworkStackProps) {
    super(scope, id, props);

    // Three-tier VPC: ECS/Lambda run in PrivateApp (needs NAT egress for ECR
    // pulls and outbound AWS SDK calls); Aurora sits in PrivateDb, which is
    // PRIVATE_ISOLATED — no NAT route at all — so the database has no path to
    // or from the public internet, satisfying "DB in private subnets"
    // literally rather than just "not directly internet-facing."
    this.vpc = new ec2.Vpc(this, 'SentinelOpsVpc', {
      vpcName: 'sentinelops-vpc',
      maxAzs: 2,
      // Single NAT gateway is a deliberate cost tradeoff (one fewer AZ of NAT
      // redundancy) — see docs/security/security-assumptions.md.
      natGateways: props.config.natGateways,
      subnetConfiguration: [
        { name: 'Public', subnetType: ec2.SubnetType.PUBLIC, cidrMask: 24 },
        { name: 'PrivateApp', subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS, cidrMask: 24 },
        { name: 'PrivateDb', subnetType: ec2.SubnetType.PRIVATE_ISOLATED, cidrMask: 24 },
      ],
    });

    // Security groups reference each other by ID, never by CIDR, so access is
    // always "the other tier's SG" rather than an IP range that could drift.
    this.albSecurityGroup = new ec2.SecurityGroup(this, 'AlbSecurityGroup', {
      vpc: this.vpc,
      description: 'SentinelOps API ALB - private, reachable only from the API Gateway VPC Link',
      allowAllOutbound: false,
    });
    this.ecsSecurityGroup = new ec2.SecurityGroup(this, 'ApiEcsSecurityGroup', {
      vpc: this.vpc,
      description: 'SentinelOps API ECS tasks and DB-touching Lambda workers',
      allowAllOutbound: false,
    });
    // The RDS/rotation security groups live in DatabaseStack, not here — the
    // SecretRotation construct mutates the RDS security group's ingress
    // rules using a dynamic (cluster-endpoint-derived) port token, and doing
    // that across a stack boundary creates a real circular dependency
    // (DatabaseStack already depends on this stack for the VPC/ecsSecurityGroup).
    // Keeping both security groups in the same stack as the cluster avoids it.
    // API Gateway's VpcLink ENIs — the only thing allowed to reach the now-
    // private API ALB. See ApiStack for the HTTP API + VpcLink construction.
    this.vpcLinkSecurityGroup = new ec2.SecurityGroup(this, 'VpcLinkSecurityGroup', {
      vpc: this.vpc,
      description: 'API Gateway VpcLink for the private API ALB',
      allowAllOutbound: false,
    });

    this.vpcLinkSecurityGroup.connections.allowTo(
      this.albSecurityGroup,
      ec2.Port.tcp(5000),
      'VPC Link to private API ALB',
    );

    this.ecsSecurityGroup.connections.allowFrom(
      this.albSecurityGroup,
      ec2.Port.tcp(5000),
      'API containers from ALB',
    );
    // CIDR-scoped (to this VPC only), not SG-reference-scoped (to
    // DatabaseStack's rdsSecurityGroup specifically) — deliberately, so this
    // rule stays entirely self-contained within NetworkStack. Referencing
    // rdsSecurityGroup's ID here would make NetworkStack depend on
    // DatabaseStack, which already depends on NetworkStack for the VPC and
    // this very security group — a cycle. DatabaseStack adds the matching
    // SG-reference-scoped ingress rule on its own rdsSecurityGroup instead
    // (see database-stack.ts), which is one-directional and cycle-free.
    this.ecsSecurityGroup.addEgressRule(
      ec2.Peer.ipv4(this.vpc.vpcCidrBlock),
      ec2.Port.tcp(5432),
      "Postgres (Aurora lives in this VPC's isolated subnet)",
    );
    // AWS SDK calls (Cognito, S3, EventBridge, Secrets Manager, ECR) leave the
    // VPC via the NAT gateway on 443 — no broader egress than that.
    this.ecsSecurityGroup.addEgressRule(
      ec2.Peer.anyIpv4(),
      ec2.Port.tcp(443),
      'AWS SDK / ECR calls over HTTPS',
    );
  }
}
