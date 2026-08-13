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

    // Both security groups live here, in the same stack as the cluster —
    // not in NetworkStack — because SecretRotation (below) mutates the RDS
    // security group's ingress rules using a dynamic, cluster-endpoint-
    // derived port token. Doing that across a stack boundary creates a real
    // circular dependency (DatabaseStack already depends on NetworkStack for
    // the VPC/ecsSecurityGroup); keeping them together avoids it entirely.
    const rdsSecurityGroup = new ec2.SecurityGroup(this, 'RdsSecurityGroup', {
      vpc: props.vpc,
      description: 'SentinelOps Aurora PostgreSQL cluster',
      allowAllOutbound: false,
    });
    // AWS's own SecretsManagerRDSPostgreSQLRotationSingleUser Lambda — given
    // its own scoped security group (rather than the allow-all-outbound
    // default addRotationSingleUser() would create) via the explicit
    // SecretRotation construct below.
    const rotationSecurityGroup = new ec2.SecurityGroup(this, 'RotationSecurityGroup', {
      vpc: props.vpc,
      description: 'Aurora credential rotation Lambda',
      allowAllOutbound: false,
    });

    // One-directional on purpose: this only mutates rdsSecurityGroup (owned by
    // this stack), referencing ecsSecurityGroup's ID as an imported value from
    // NetworkStack — a dependency that already exists via the `ecsSecurityGroup`
    // prop. Using `.connections.allowTo/allowFrom` here instead (which mutates
    // *both* SGs) would add an egress rule to ecsSecurityGroup in NetworkStack
    // referencing this stack's rdsSecurityGroup ID, creating the same
    // NetworkStack↔DatabaseStack cycle SecretRotation's automatic wiring did —
    // see the matching egress-by-CIDR rule on ecsSecurityGroup in NetworkStack,
    // which is the other half of this connection and stays cycle-free by not
    // referencing this stack's resources at all.
    rdsSecurityGroup.addIngressRule(
      ec2.Peer.securityGroupId(props.ecsSecurityGroup.securityGroupId),
      ec2.Port.tcp(5432),
      'Postgres from API containers/Lambda workers',
    );
    // The rotation Lambda calls the Secrets Manager API itself (to write the
    // new password) in addition to connecting to Postgres — same egress
    // shape as ecsSecurityGroup, over the NAT gateway.
    rotationSecurityGroup.addEgressRule(
      ec2.Peer.anyIpv4(),
      ec2.Port.tcp(443),
      'Secrets Manager API over HTTPS',
    );

    // Requires TLS on the Postgres wire protocol itself, not just at the ALB —
    // Npgsql connection strings built from this cluster's secret must include
    // `SSL Mode=Require;Trust Server Certificate=true`.
    const engine = rds.DatabaseClusterEngine.auroraPostgres({
      version: rds.AuroraPostgresEngineVersion.VER_16_13,
    });
    const dbParameterGroup = new rds.ParameterGroup(this, 'DatabaseParameterGroup', {
      engine,
      parameters: { 'rds.force_ssl': '1' },
    });

    // Aurora Serverless v2 — auto-scales capacity (ACUs) with load and can
    // scale down close to zero, which fits dev/staging's cost profile better
    // than fixed provisioned instances. No reader instance is added (single-
    // instance cost tradeoff, same as the previous single-AZ RDS setup — see
    // docs/security/security-assumptions.md).
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
      // fromGeneratedSecret creates the Secrets Manager secret every Postgres
      // consumer across every stack (ECS in ApiStack, every worker Lambda in
      // EventProcessingStack/ApiStack) reads from at runtime. Deliberately
      // NOT encrypted with the shared dataKey (unlike RDS storage above):
      // Secret.grantRead() always grants KMS decrypt via a ViaServicePrincipal
      // wrapper (Secrets Manager calls KMS on the caller's behalf), which
      // can't hold an identity policy and so always falls back to mutating
      // the key's own resource policy with the grantee's role ARN. With every
      // worker Lambda across two downstream stacks calling grantRead(), that
      // creates a real circular dependency back onto whichever stack owns the
      // key. Secrets Manager's default AWS-owned key avoids this — the secret
      // is still encrypted at rest, just not with our customer-managed key.
      credentials: rds.Credentials.fromGeneratedSecret('sentinelops_admin', {
        secretName: 'sentinelops/rds-credentials',
      }),
      defaultDatabaseName: 'sentinelops',
      deletionProtection: props.config.envName !== 'dev',
      removalPolicy: props.config.removalPolicy.dataBearing,
      backup: { retention: cdk.Duration.days(7) },
    });
    this.secret = this.cluster.secret!;

    // Deployed AWS's own single-user rotation Lambda into PrivateApp — this
    // is the concrete mechanism behind "rotate credentials."
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
