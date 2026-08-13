import * as cdk from 'aws-cdk-lib/core';
import * as s3 from 'aws-cdk-lib/aws-s3';
import * as logs from 'aws-cdk-lib/aws-logs';
import * as cloudtrail from 'aws-cdk-lib/aws-cloudtrail';
import * as cloudwatch from 'aws-cdk-lib/aws-cloudwatch';
import * as kms from 'aws-cdk-lib/aws-kms';
import { Construct } from 'constructs';
import { EnvironmentConfig } from '../config/environments';

export interface ObservabilityStackProps extends cdk.StackProps {
  config: EnvironmentConfig;
  dataKey: kms.IKey;
}

export class ObservabilityStack extends cdk.Stack {
  public readonly apiLogGroup: logs.LogGroup;
  public readonly trailBucket: s3.Bucket;

  constructor(scope: Construct, id: string, props: ObservabilityStackProps) {
    super(scope, id, props);

    this.apiLogGroup = new logs.LogGroup(this, 'ApiLogGroup', {
      logGroupName: '/sentinelops/api',
      retention: props.config.logRetention,
      removalPolicy: props.config.removalPolicy.compute,
    });

    // --- Audit trail -----------------------------------------------------------
    // Account-level API-call audit trail — distinct from and complementary to
    // apps/api's Common/IAuditLogger application-level audit rows (which
    // record business actions like "user X changed incident Y's status", not
    // "someone called iam:CreateUser"). Multi-region + global-service-events so
    // IAM/STS calls (which are always us-east-1) are captured too.
    this.trailBucket = new s3.Bucket(this, 'CloudTrailBucket', {
      blockPublicAccess: s3.BlockPublicAccess.BLOCK_ALL,
      encryption: s3.BucketEncryption.KMS,
      encryptionKey: props.dataKey,
      enforceSSL: true,
      removalPolicy: cdk.RemovalPolicy.RETAIN,
      lifecycleRules: [{ id: 'ExpireOldTrailLogs', expiration: cdk.Duration.days(365) }],
    });

    new cloudtrail.Trail(this, 'SentinelOpsTrail', {
      trailName: 'sentinelops-trail',
      bucket: this.trailBucket,
      isMultiRegionTrail: true,
      includeGlobalServiceEvents: true,
      encryptionKey: props.dataKey,
      sendToCloudWatchLogs: true,
      cloudWatchLogsRetention: logs.RetentionDays.ONE_YEAR,
    });

    new cdk.CfnOutput(this, 'CloudTrailBucketName', { value: this.trailBucket.bucketName });

    // --- CloudWatch dashboard ---------------------------------------------------
    // Built entirely from string-named metrics (namespace + dimension names
    // known ahead of time from the other stacks' fixed resource names), not
    // by importing constructs from ApiStack/EventProcessingStack/FrontendStack
    // — this deliberately avoids a reverse dependency onto stacks that are
    // built after this one (see the dependency graph in the implementation
    // plan). If a resource's fixed name ever changes, this dashboard's widgets
    // need a matching update.
    new cloudwatch.Dashboard(this, 'SentinelOpsDashboard', {
      dashboardName: `sentinelops-${props.config.envName}`,
      widgets: [
        [
          new cloudwatch.GraphWidget({
            title: 'API — ECS CPU/Memory',
            left: [
              new cloudwatch.Metric({
                namespace: 'AWS/ECS',
                metricName: 'CPUUtilization',
                dimensionsMap: {
                  ClusterName: 'sentinelops-cluster',
                  ServiceName: 'sentinelops-api',
                },
                statistic: 'Average',
              }),
              new cloudwatch.Metric({
                namespace: 'AWS/ECS',
                metricName: 'MemoryUtilization',
                dimensionsMap: {
                  ClusterName: 'sentinelops-cluster',
                  ServiceName: 'sentinelops-api',
                },
                statistic: 'Average',
              }),
            ],
          }),
          new cloudwatch.GraphWidget({
            title: 'Worker Lambda errors',
            left: [
              new cloudwatch.Metric({
                namespace: 'AWS/Lambda',
                metricName: 'Errors',
                statistic: 'Sum',
              }),
            ],
          }),
        ],
        [
          new cloudwatch.GraphWidget({
            title: 'SQS — approximate visible messages',
            left: [
              new cloudwatch.Metric({
                namespace: 'AWS/SQS',
                metricName: 'ApproximateNumberOfMessagesVisible',
                dimensionsMap: { QueueName: 'sentinelops-alert-validation-dlq' },
                statistic: 'Maximum',
              }),
            ],
          }),
          new cloudwatch.GraphWidget({
            title: 'API Gateway — 4xx/5xx',
            left: [
              new cloudwatch.Metric({
                namespace: 'AWS/ApiGateway',
                metricName: '4xx',
                statistic: 'Sum',
              }),
              new cloudwatch.Metric({
                namespace: 'AWS/ApiGateway',
                metricName: '5xx',
                statistic: 'Sum',
              }),
            ],
          }),
        ],
      ],
    });
  }
}
