import * as cdk from 'aws-cdk-lib/core';
import * as ec2 from 'aws-cdk-lib/aws-ec2';
import * as rds from 'aws-cdk-lib/aws-rds';
import * as kms from 'aws-cdk-lib/aws-kms';
import * as secretsmanager from 'aws-cdk-lib/aws-secretsmanager';
import { Construct } from 'constructs';
import { EnvironmentConfig } from '../config/environments';

export interface DatabaseStackProps extends cdk.StackProps {
  config: EnvironmentConfig;
  vpc: ec2.IVpc;
  ecsSecurityGroup: ec2.ISecurityGroup;
  dataKey: kms.IKey;
}

export class DatabaseStack extends cdk.Stack {
  public readonly cluster: rds.DatabaseCluster;
  public readonly secret: secretsmanager.ISecret;

  constructor(scope: Construct, id: string, props: DatabaseStackProps) {
    super(scope, id, props);

    // Both security groups live here, not in NetworkStack: SecretRotation (below)
    // mutates the RDS SG's ingress rules with a dynamic, cluster-endpoint-derived
    // port token, and doing that across a stack boundary would create a circular
    // dependency (DatabaseStack already depends on NetworkStack for the VPC).
    const rdsSecurityGroup = new ec2.SecurityGroup(this, 'RdsSecurityGroup', {
      vpc: props.vpc,
      description: 'SentinelOps Aurora PostgreSQL cluster',
      allowAllOutbound: false,
    });
    // Scoped security group for the rotation Lambda, instead of the allow-all-outbound
    // default addRotationSingleUser() would create.
    const rotationSecurityGroup = new ec2.SecurityGroup(this, 'RotationSecurityGroup', {
      vpc: props.vpc,
      description: 'Aurora credential rotation Lambda',
      allowAllOutbound: false,
    });

    // One-directional on purpose: mutates only rdsSecurityGroup (owned by this stack).
    // Using .connections.allowTo/allowFrom instead would mutate ecsSecurityGroup in
    // NetworkStack too, creating a NetworkStack<->DatabaseStack cycle. The matching
    // egress-by-CIDR rule lives on ecsSecurityGroup in network-stack.ts.
    rdsSecurityGroup.addIngressRule(
      ec2.Peer.securityGroupId(props.ecsSecurityGroup.securityGroupId),
      ec2.Port.tcp(5432),
      'Postgres from API containers/Lambda workers',
    );
    // The rotation Lambda also calls the Secrets Manager API to write the new password.
    rotationSecurityGroup.addEgressRule(
      ec2.Peer.anyIpv4(),
      ec2.Port.tcp(443),
      'Secrets Manager API over HTTPS',
    );

    // Requires TLS on the Postgres wire protocol itself: Npgsql connection strings
    // built from this cluster's secret must include SSL Mode=Require.
    const engine = rds.DatabaseClusterEngine.auroraPostgres({
      version: rds.AuroraPostgresEngineVersion.VER_16_13,
    });
    const dbParameterGroup = new rds.ParameterGroup(this, 'DatabaseParameterGroup', {
      engine,
      parameters: { 'rds.force_ssl': '1' },
    });

    // Aurora Serverless v2 auto-scales ACUs with load and scales down close to zero.
    // No reader instance added (single-instance cost tradeoff).
    this.cluster = new rds.DatabaseCluster(this, 'SentinelOpsDatabase', {
      clusterIdentifier: 'sentinelops-db',
      engine,
      vpc: props.vpc,
      vpcSubnets: { subnetType: ec2.SubnetType.PRIVATE_ISOLATED },
      securityGroups: [rdsSecurityGroup],
      parameterGroup: dbParameterGroup,
      serverlessV2MinCapacity: props.config.aurora.minCapacityAcu,
      serverlessV2MaxCapacity: props.config.aurora.maxCapacityAcu,
      writer: rds.ClusterInstance.serverlessV2('Writer', { parameterGroup: dbParameterGroup }),
      storageEncrypted: true,
      storageEncryptionKey: props.dataKey,
      // Not encrypted with the shared dataKey (unlike RDS storage above): Secret.grantRead()
      // grants KMS decrypt by mutating the key's resource policy, and with worker Lambdas
      // in downstream stacks calling grantRead(), that creates a circular dependency back
      // onto the key's owning stack. Secrets Manager's default AWS-owned key avoids this;
      // the secret is still encrypted at rest, just not with our customer-managed key.
      credentials: rds.Credentials.fromGeneratedSecret('sentinelops_admin', {
        secretName: 'sentinelops/rds-credentials',
      }),
      defaultDatabaseName: 'sentinelops',
      deletionProtection: props.config.envName !== 'dev',
      removalPolicy: props.config.removalPolicy.dataBearing,
      backup: { retention: cdk.Duration.days(7) },
    });
    this.secret = this.cluster.secret!;

    new secretsmanager.SecretRotation(this, 'DatabaseSecretRotation', {
      secret: this.secret,
      application: secretsmanager.SecretRotationApplication.POSTGRES_ROTATION_SINGLE_USER,
      vpc: props.vpc,
      vpcSubnets: { subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS },
      securityGroup: rotationSecurityGroup,
      target: this.cluster,
      automaticallyAfter: cdk.Duration.days(30),
    });

    new cdk.CfnOutput(this, 'DatabaseSecretArn', { value: this.secret.secretArn });
    new cdk.CfnOutput(this, 'DatabaseEndpoint', { value: this.cluster.clusterEndpoint.hostname });
  }
}
