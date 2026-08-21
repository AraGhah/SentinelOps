import * as cdk from 'aws-cdk-lib/core';
import * as s3 from 'aws-cdk-lib/aws-s3';
import * as dynamodb from 'aws-cdk-lib/aws-dynamodb';
import * as kms from 'aws-cdk-lib/aws-kms';
import * as iam from 'aws-cdk-lib/aws-iam';
import { Construct } from 'constructs';
import { EnvironmentConfig } from '../config/environments';

export interface StorageStackProps extends cdk.StackProps {
  config: EnvironmentConfig;
}

export class StorageStack extends cdk.Stack {
  public readonly dataKey: kms.Key;
  public readonly attachmentsBucket: s3.Bucket;
  public readonly reportsBucket: s3.Bucket;
  public readonly fingerprintTable: dynamodb.Table;
  public readonly connectionsTable: dynamodb.Table;

  constructor(scope: Construct, id: string, props: StorageStackProps) {
    super(scope, id, props);

    // Shared CMK for Aurora storage, Secrets Manager secrets, ECR images, CloudTrail
    // logs, and both S3 buckets below. Other stacks take this as a kms.IKey prop.
    this.dataKey = new kms.Key(this, 'SentinelOpsDataKey', {
      alias: 'sentinelops/data',
      description:
        'CMK for Aurora storage, Secrets Manager secrets, ECR images, CloudTrail logs, and S3 buckets',
      enableKeyRotation: true,
      removalPolicy: cdk.RemovalPolicy.RETAIN,
    });

    // --- File attachments ---------------------------------------------------
    // Versioned so a bad presigned-upload overwrite (or GuardDuty quarantine action)
    // doesn't lose the original object. apps/api presigns PUT/GET URLs using its own
    // IAM identity (ApiTaskRole), never holds bucket credentials directly.
    this.attachmentsBucket = new s3.Bucket(this, 'AttachmentsBucket', {
      blockPublicAccess: s3.BlockPublicAccess.BLOCK_ALL,
      encryption: s3.BucketEncryption.KMS,
      encryptionKey: this.dataKey,
      enforceSSL: true,
      versioned: true,
      lifecycleRules: [
        // Presigned PUTs that never complete would otherwise sit around forever.
        {
          id: 'AbortIncompleteMultipartUploads',
          abortIncompleteMultipartUploadAfter: cdk.Duration.days(7),
        },
        // Bounds how long a superseded/deleted version's bytes still cost anything.
        { id: 'ExpireNoncurrentVersions', noncurrentVersionExpiration: cdk.Duration.days(90) },
      ],
      // apps/web PUTs the file directly to S3 via a presigned URL (never through
      // apps/api), so it needs CORS like any other cross-origin browser request.
      // ETag exposed for multipart/consistency checks.
      cors: [
        {
          allowedOrigins: props.config.corsAllowedOrigins,
          allowedMethods: [s3.HttpMethods.PUT, s3.HttpMethods.GET],
          allowedHeaders: ['*'],
          exposedHeaders: ['ETag'],
          maxAge: 3600,
        },
      ],
      removalPolicy: props.config.removalPolicy.dataBearing,
    });

    new cdk.CfnOutput(this, 'AttachmentsBucketName', {
      value: this.attachmentsBucket.bucketName,
      description:
        'S3 bucket for incident attachments, set as Aws__Attachments__BucketName on apps/api',
    });

    // GuardDuty Malware Protection scans every object PUT and emits a finding on the
    // account's default EventBridge bus. No L2/typed L1 construct exists yet in the
    // pinned CDK version, so it's declared via the CFN escape hatch.
    const malwareProtectionRole = new iam.Role(this, 'AttachmentsMalwareProtectionRole', {
      assumedBy: new iam.ServicePrincipal('malware-protection-plan.guardduty.amazonaws.com'),
    });
    this.attachmentsBucket.grantRead(malwareProtectionRole);
    malwareProtectionRole.addToPolicy(
      new iam.PolicyStatement({
        actions: ['s3:PutObjectTagging', 's3:GetObjectTagging', 's3:GetBucketLocation'],
        resources: [this.attachmentsBucket.bucketArn, this.attachmentsBucket.arnForObjects('*')],
      }),
    );
    malwareProtectionRole.addToPolicy(
      new iam.PolicyStatement({
        actions: ['iam:CreateServiceLinkedRole'],
        resources: [
          'arn:aws:iam::*:role/aws-service-role/malware-protection-plan.guardduty.amazonaws.com/*',
        ],
        conditions: {
          StringLike: { 'iam:AWSServiceName': 'malware-protection-plan.guardduty.amazonaws.com' },
        },
      }),
    );

    new cdk.CfnResource(this, 'AttachmentsMalwareProtectionPlan', {
      type: 'AWS::GuardDuty::MalwareProtectionPlan',
      properties: {
        Role: malwareProtectionRole.roleArn,
        ProtectedResource: { S3Bucket: { BucketName: this.attachmentsBucket.bucketName } },
        Actions: { Tagging: { Status: 'ENABLED' } },
      },
    });

    // --- Post-incident reports -----------------------------------------------
    // Like AttachmentsBucket but no malware protection plan: nothing here is a
    // client-supplied upload, apps/api renders and PUTs the bytes itself.
    this.reportsBucket = new s3.Bucket(this, 'ReportsBucket', {
      blockPublicAccess: s3.BlockPublicAccess.BLOCK_ALL,
      encryption: s3.BucketEncryption.KMS,
      encryptionKey: this.dataKey,
      enforceSSL: true,
      versioned: true,
      lifecycleRules: [
        { id: 'ExpireNoncurrentVersions', noncurrentVersionExpiration: cdk.Duration.days(90) },
      ],
      removalPolicy: props.config.removalPolicy.dataBearing,
    });

    new cdk.CfnOutput(this, 'ReportsBucketName', {
      value: this.reportsBucket.bucketName,
      description:
        'S3 bucket for generated post-incident reports, set as Aws__Reports__BucketName on apps/api',
    });

    // --- DynamoDB tables ------------------------------------------------------
    // Active alert-fingerprint index for deduplication. Pure cache (Postgres
    // Alert/Incident rows are the durable record), so PAY_PER_REQUEST + TTL is enough;
    // PITR is cheap enough on this table to also cover accidental deletes.
    this.fingerprintTable = new dynamodb.Table(this, 'AlertFingerprintTable', {
      tableName: 'sentinelops-alert-fingerprints',
      partitionKey: { name: 'Fingerprint', type: dynamodb.AttributeType.STRING },
      billingMode: dynamodb.BillingMode.PAY_PER_REQUEST,
      timeToLiveAttribute: 'ExpiresAt',
      pointInTimeRecoverySpecification: { pointInTimeRecoveryEnabled: true },
      removalPolicy: props.config.removalPolicy.dataBearing,
    });

    new cdk.CfnOutput(this, 'AlertFingerprintTableName', {
      value: this.fingerprintTable.tableName,
    });

    // One item per live WebSocket connection, queried by org via the GSI when
    // broadcasting. TTL backstops connections that vanish without a clean $disconnect.
    // PITR enabled: unlike the fingerprint table, there's no other copy to rebuild from.
    this.connectionsTable = new dynamodb.Table(this, 'DashboardConnectionsTable', {
      tableName: 'sentinelops-dashboard-connections',
      partitionKey: { name: 'ConnectionId', type: dynamodb.AttributeType.STRING },
      billingMode: dynamodb.BillingMode.PAY_PER_REQUEST,
      timeToLiveAttribute: 'ExpiresAt',
      pointInTimeRecoverySpecification: { pointInTimeRecoveryEnabled: true },
      removalPolicy: props.config.removalPolicy.dataBearing,
    });
    this.connectionsTable.addGlobalSecondaryIndex({
      indexName: 'OrganizationId-index',
      partitionKey: { name: 'OrganizationId', type: dynamodb.AttributeType.STRING },
    });
  }
}
