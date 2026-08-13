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

    // Shared CMK for everything at rest across every stack that isn't already
    // covered by an AWS-managed key: Aurora storage, Secrets Manager secrets,
    // ECR images, CloudTrail logs, and both S3 buckets below. Consumers in
    // other stacks (DatabaseStack, ObservabilityStack, ApiStack) take this as
    // a kms.IKey constructor prop.
    this.dataKey = new kms.Key(this, 'SentinelOpsDataKey', {
      alias: 'sentinelops/data',
      description:
        'CMK for Aurora storage, Secrets Manager secrets, ECR images, CloudTrail logs, and S3 buckets',
      enableKeyRotation: true,
      removalPolicy: cdk.RemovalPolicy.RETAIN,
    });

    // --- File attachments ---------------------------------------------------
    // Private, encrypted, versioned so a bad presigned-upload overwrite (or a
    // GuardDuty quarantine action) doesn't lose the original object. apps/api
    // never gets bucket credentials of its own here — it presigns PUT/GET URLs
    // using whatever IAM identity it runs under (ApiStack's ApiTaskRole).
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
        // Attachment deletion (AttachmentsController.Delete) removes the current
        // version; this bounds how long a superseded/deleted version's bytes
        // still cost anything.
        { id: 'ExpireNoncurrentVersions', noncurrentVersionExpiration: cdk.Duration.days(90) },
      ],
      removalPolicy: props.config.removalPolicy.dataBearing,
    });

    new cdk.CfnOutput(this, 'AttachmentsBucketName', {
      value: this.attachmentsBucket.bucketName,
      description:
        'S3 bucket for incident attachments — set as Aws__Attachments__BucketName on apps/api',
    });

    // GuardDuty Malware Protection for S3 scans every object PUT to the
    // bucket and emits a finding on the account's default EventBridge bus —
    // this is the "scan uploaded files" requirement, without running any
    // scanning code of our own. No aws-cdk-lib L2 (or typed L1) construct for
    // this resource exists yet in the pinned CDK version, so it's declared
    // via the CFN escape hatch.
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
    // Private/encrypted/versioned like AttachmentsBucket, but no malware
    // protection plan: unlike attachments, nothing here is a client-supplied
    // upload — apps/api renders the HTML/PDF itself and PUTs the bytes directly.
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
        'S3 bucket for generated post-incident reports — set as Aws__Reports__BucketName on apps/api',
    });

    // --- DynamoDB tables ------------------------------------------------------
    // Active alert-fingerprint index for the deduplication engine: one item
    // per (org, normalized title, error code, service, environment) hash,
    // holding the incident it currently maps to. TTL-expired items just stop
    // matching — they aren't a durable record, Postgres is (Alert/Incident
    // rows), so PAY_PER_REQUEST plus TTL is enough here.
    this.fingerprintTable = new dynamodb.Table(this, 'AlertFingerprintTable', {
      tableName: 'sentinelops-alert-fingerprints',
      partitionKey: { name: 'Fingerprint', type: dynamodb.AttributeType.STRING },
      billingMode: dynamodb.BillingMode.PAY_PER_REQUEST,
      timeToLiveAttribute: 'ExpiresAt',
      removalPolicy: props.config.removalPolicy.dataBearing,
    });

    new cdk.CfnOutput(this, 'AlertFingerprintTableName', {
      value: this.fingerprintTable.tableName,
    });

    // One item per live WebSocket connection, queried by org via the GSI when
    // broadcasting — TTL is a backstop for connections that vanish without a
    // clean $disconnect.
    this.connectionsTable = new dynamodb.Table(this, 'DashboardConnectionsTable', {
      tableName: 'sentinelops-dashboard-connections',
      partitionKey: { name: 'ConnectionId', type: dynamodb.AttributeType.STRING },
      billingMode: dynamodb.BillingMode.PAY_PER_REQUEST,
      timeToLiveAttribute: 'ExpiresAt',
      removalPolicy: props.config.removalPolicy.dataBearing,
    });
    this.connectionsTable.addGlobalSecondaryIndex({
      indexName: 'OrganizationId-index',
      partitionKey: { name: 'OrganizationId', type: dynamodb.AttributeType.STRING },
    });
  }
}
