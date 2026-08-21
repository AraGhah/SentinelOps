import * as cdk from 'aws-cdk-lib/core';
import * as s3 from 'aws-cdk-lib/aws-s3';
import * as logs from 'aws-cdk-lib/aws-logs';
import * as cloudtrail from 'aws-cdk-lib/aws-cloudtrail';
import * as cloudwatch from 'aws-cdk-lib/aws-cloudwatch';
import * as cwActions from 'aws-cdk-lib/aws-cloudwatch-actions';
import * as sns from 'aws-cdk-lib/aws-sns';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as budgets from 'aws-cdk-lib/aws-budgets';
import * as kms from 'aws-cdk-lib/aws-kms';
import { Construct } from 'constructs';
import { EnvironmentConfig } from '../config/environments';

export interface ObservabilityStackProps extends cdk.StackProps {
  config: EnvironmentConfig;
  dataKey: kms.IKey;
}

// SQS queue name suffixes (see EventProcessingStack's `workerQueue` calls) —
// every one gets a "does this queue have a stuck backlog / DLQ messages"
// alarm below. Kept as a plain string list rather than importing
// EventProcessingStack constructs: this stack is deployed *before*
// EventProcessingStack (see bin/infrastructure.ts), so it can only reference
// the other stacks' resources by their fixed, predictable names.
const WORKER_QUEUE_NAMES = [
  'alert-validation',
  'deduplication',
  'incident-creation',
  'responder-assignment',
  'notification',
  'analytics',
  'audit-log',
  'escalation-restart',
  'attachment-scan',
  'dashboard-broadcast',
];

// Every worker Lambda's fixed FunctionName (see EventProcessingStack's
// `workerFunction`/hand-built escalation functions, and ApiStack's
// `dashboardFunction`) — same "deploy order" reasoning as above.
const WORKER_FUNCTION_NAMES = [
  'alert-validation',
  'deduplication',
  'incident-creation',
  'responder-assignment',
  'notification',
  'analytics',
  'audit-log',
  'attachment-scan',
  'escalation',
  'escalation-restart',
  'dashboard-connect',
  'dashboard-disconnect',
  'dashboard-broadcast',
];

export class ObservabilityStack extends cdk.Stack {
  public readonly apiLogGroup: logs.LogGroup;
  public readonly trailBucket: s3.Bucket;
  public readonly alarmTopic: sns.Topic;

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

    // --- Alarm notifications -----------------------------------------------------
    // No subscription added here — who gets paged is an operational decision
    // for whoever owns this account, not something to hardcode into the
    // stack. Subscribe an email/Slack-webhook-via-Lambda/PagerDuty endpoint
    // post-deploy: `aws sns subscribe --topic-arn <AlarmTopicArn> ...`
    // (output below). Same pattern as CiCdStack's production-environment
    // reviewers: a repo/account-admin action, not something CDK code can set
    // up unattended.
    this.alarmTopic = new sns.Topic(this, 'AlarmTopic', { topicName: 'sentinelops-alarms' });
    new cdk.CfnOutput(this, 'AlarmTopicArn', {
      value: this.alarmTopic.topicArn,
      description:
        'Subscribe an email/webhook endpoint to this topic to receive alarm notifications.',
    });
    const alarmAction = new cwActions.SnsAction(this.alarmTopic);

    // --- AWS Budget ----------------------------------------------------------
    // Reuses AlarmTopic (above) as the notification channel — one place to
    // subscribe an email/webhook for both operational alarms and billing
    // warnings, rather than a second topic. AWS Budgets requires an explicit
    // resource policy on the topic before it's allowed to publish to it (the
    // service isn't implicitly trusted the way CloudWatch alarm actions are).
    this.alarmTopic.addToResourcePolicy(
      new iam.PolicyStatement({
        sid: 'AllowBudgetsToPublish',
        effect: iam.Effect.ALLOW,
        principals: [new iam.ServicePrincipal('budgets.amazonaws.com')],
        actions: ['sns:Publish'],
        resources: [this.alarmTopic.topicArn],
      }),
    );

    const budgetNotification = (
      thresholdPercent: number,
      notificationType: 'ACTUAL' | 'FORECASTED',
    ) => ({
      notification: {
        notificationType,
        comparisonOperator: 'GREATER_THAN',
        threshold: thresholdPercent,
        thresholdType: 'PERCENTAGE',
      },
      subscribers: [{ subscriptionType: 'SNS', address: this.alarmTopic.topicArn }],
    });

    // costFilters scopes this budget to just this environment's tagged spend
    // (see the Environment tag every stack gets in bin/infrastructure.ts) —
    // without it, a per-environment budget would actually track the whole
    // account's spend, which is wrong once more than one environment exists
    // in the same account.
    new budgets.CfnBudget(this, 'MonthlyBudget', {
      budget: {
        budgetName: `sentinelops-${props.config.envName}-monthly`,
        budgetType: 'COST',
        timeUnit: 'MONTHLY',
        budgetLimit: { amount: props.config.monthlyBudgetUsd, unit: 'USD' },
        costFilters: { TagKeyValue: [`user:Environment$${props.config.envName}`] },
      },
      // 80% actual spend is a real warning; 100% forecasted catches a runaway
      // cost trend early enough in the month to still do something about it.
      notificationsWithSubscribers: [
        budgetNotification(80, 'ACTUAL'),
        budgetNotification(100, 'FORECASTED'),
      ],
    });

    // --- Custom metric helpers ----------------------------------------------
    // SentinelOps/Api and SentinelOps/Workers are populated by
    // MetricsEmitter (apps/api/Common) and WorkerMetrics
    // (apps/workers/SentinelOps.Workers.Shared) — both emit CloudWatch
    // Embedded Metric Format log lines, which CloudWatch turns into these
    // metrics with no PutMetricData call and no stack-ordering dependency
    // (unlike an ALB or API Gateway resource, a metric namespace is just a
    // string, so this stack can reference it even though it's deployed
    // before ApiStack/EventProcessingStack create the things that emit it).
    const apiMetric = (
      metricName: string,
      statistic: string,
      dimensions?: Record<string, string>,
    ) =>
      new cloudwatch.Metric({
        namespace: 'SentinelOps/Api',
        metricName,
        statistic,
        dimensionsMap: dimensions,
      });
    const workerMetric = (
      metricName: string,
      statistic: string,
      dimensions?: Record<string, string>,
    ) =>
      new cloudwatch.Metric({
        namespace: 'SentinelOps/Workers',
        metricName,
        statistic,
        dimensionsMap: dimensions,
      });
    const queueDepthMetric = (queueName: string) =>
      new cloudwatch.Metric({
        namespace: 'AWS/SQS',
        metricName: 'ApproximateNumberOfMessagesVisible',
        dimensionsMap: { QueueName: `sentinelops-${queueName}` },
        statistic: 'Maximum',
        label: queueName,
      });
    const oldestMessageMetric = (queueName: string) =>
      new cloudwatch.Metric({
        namespace: 'AWS/SQS',
        metricName: 'ApproximateAgeOfOldestMessage',
        dimensionsMap: { QueueName: `sentinelops-${queueName}` },
        statistic: 'Maximum',
        label: queueName,
      });
    const dlqDepthMetric = (queueName: string) =>
      new cloudwatch.Metric({
        namespace: 'AWS/SQS',
        metricName: 'ApproximateNumberOfMessagesVisible',
        dimensionsMap: { QueueName: `sentinelops-${queueName}-dlq` },
        statistic: 'Maximum',
        label: `${queueName}-dlq`,
      });
    const lambdaErrorsMetric = (functionName: string) =>
      new cloudwatch.Metric({
        namespace: 'AWS/Lambda',
        metricName: 'Errors',
        dimensionsMap: { FunctionName: `sentinelops-${functionName}` },
        statistic: 'Sum',
        label: functionName,
      });

    // --- CloudWatch alarms -------------------------------------------------------
    let alarmIndex = 0;
    const alarm = (
      description: string,
      metric: cloudwatch.IMetric,
      threshold: number,
      comparisonOperator: cloudwatch.ComparisonOperator,
      evaluationPeriods = 3,
    ) => {
      alarmIndex += 1;
      const a = new cloudwatch.Alarm(this, `Alarm${alarmIndex}`, {
        alarmName: `sentinelops-${props.config.envName}-${description}`,
        metric,
        threshold,
        comparisonOperator,
        evaluationPeriods,
        treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
      });
      a.addAlarmAction(alarmAction);
      return a;
    };

    alarm(
      'api-high-error-rate',
      apiMetric('HttpErrors', 'Sum'),
      20,
      cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD,
      2,
    );
    alarm(
      'api-high-latency',
      apiMetric('Latency', 'Average'),
      2000,
      cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD,
    );
    // Account-wide (no FunctionName dimension) — a coarse "is something on
    // fire" signal; the dashboard's per-function graph (below) is what you'd
    // actually check to find which worker. Deliberately one alarm instead of
    // WORKER_FUNCTION_NAMES.length of them, to keep alarm-count noise down.
    alarm(
      'worker-lambda-errors-high',
      new cloudwatch.Metric({ namespace: 'AWS/Lambda', metricName: 'Errors', statistic: 'Sum' }),
      5,
      cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD,
      1,
    );
    alarm(
      'notification-failures-high',
      workerMetric('NotificationFailures', 'Sum'),
      5,
      cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD,
      3,
    );
    // Npgsql's default max pool size is 100; 80 gives headroom to react
    // before the pool (and, upstream of it, Aurora's own max_connections)
    // is actually exhausted.
    alarm(
      'database-connections-high',
      apiMetric('DatabaseConnections', 'Average'),
      80,
      cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD,
    );
    alarm(
      'api-ecs-cpu-high',
      new cloudwatch.Metric({
        namespace: 'AWS/ECS',
        metricName: 'CPUUtilization',
        dimensionsMap: { ClusterName: 'sentinelops-cluster', ServiceName: 'sentinelops-api' },
        statistic: 'Average',
      }),
      85,
      cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD,
    );

    for (const queueName of WORKER_QUEUE_NAMES) {
      alarm(
        `${queueName}-dlq-not-empty`,
        dlqDepthMetric(queueName),
        0,
        cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD,
        1,
      );
      // 15 minutes — comfortably above every worker's visibility timeout
      // (15-30s) and its 5-retry redelivery window, so this only fires once a
      // message is genuinely stuck, not mid-retry.
      alarm(
        `${queueName}-oldest-message-too-old`,
        oldestMessageMetric(queueName),
        900,
        cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD,
      );
    }

    // --- CloudWatch dashboard ---------------------------------------------------
    // Built entirely from string-named metrics (namespace + dimension names
    // known ahead of time from the other stacks' fixed resource names, or
    // from the custom SentinelOps/Api and SentinelOps/Workers EMF
    // namespaces), not by importing constructs from
    // ApiStack/EventProcessingStack/FrontendStack — this deliberately avoids
    // a reverse dependency onto stacks that are built after this one (see the
    // dependency graph in bin/infrastructure.ts). If a resource's fixed name
    // ever changes, this dashboard's widgets need a matching update.
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
            title: 'API — request count & latency',
            left: [apiMetric('RequestCount', 'Sum')],
            right: [apiMetric('Latency', 'Average')],
          }),
        ],
        [
          new cloudwatch.GraphWidget({
            title: 'API — HTTP errors',
            left: [apiMetric('HttpErrors', 'Sum')],
          }),
          new cloudwatch.GraphWidget({
            title: 'Database connections',
            left: [apiMetric('DatabaseConnections', 'Average')],
          }),
        ],
        [
          new cloudwatch.GraphWidget({
            title: 'Alerts received / Incidents created / Duplicate alerts',
            left: [
              apiMetric('AlertsReceived', 'Sum'),
              workerMetric('IncidentsCreated', 'Sum'),
              workerMetric('DuplicateAlerts', 'Sum'),
            ],
          }),
          new cloudwatch.GraphWidget({
            title: 'Notification failures',
            left: [workerMetric('NotificationFailures', 'Sum')],
          }),
        ],
        [
          new cloudwatch.GraphWidget({
            title: 'Acknowledgement / resolution time (seconds)',
            left: [
              apiMetric('AcknowledgementTime', 'Average'),
              apiMetric('ResolutionTime', 'Average'),
            ],
          }),
          new cloudwatch.GraphWidget({
            title: 'Worker Lambda errors, by function',
            left: WORKER_FUNCTION_NAMES.map(lambdaErrorsMetric),
          }),
        ],
        [
          new cloudwatch.GraphWidget({
            title: 'Queue depth — approximate visible messages',
            left: WORKER_QUEUE_NAMES.map(queueDepthMetric),
          }),
          new cloudwatch.GraphWidget({
            title: 'Oldest queue message age (seconds)',
            left: WORKER_QUEUE_NAMES.map(oldestMessageMetric),
          }),
        ],
        [
          new cloudwatch.GraphWidget({
            title: 'Dead-letter queue depth',
            left: WORKER_QUEUE_NAMES.map(dlqDepthMetric),
          }),
        ],
      ],
    });
  }
}
