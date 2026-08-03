import * as cdk from 'aws-cdk-lib/core';
import * as cognito from 'aws-cdk-lib/aws-cognito';
import * as sqs from 'aws-cdk-lib/aws-sqs';
import { Construct } from 'constructs';

export class InfrastructureStack extends cdk.Stack {
  constructor(scope: Construct, id: string, props?: cdk.StackProps) {
    super(scope, id, props);

    const userPool = new cognito.UserPool(this, 'SentinelOpsUserPool', {
      userPoolName: 'sentinelops-users',
      selfSignUpEnabled: true,
      signInAliases: { email: true, username: false },
      autoVerify: { email: true },
      standardAttributes: {
        email: { required: true, mutable: true },
      },
      passwordPolicy: {
        minLength: 8,
        requireLowercase: true,
        requireUppercase: true,
        requireDigits: true,
        requireSymbols: true,
        tempPasswordValidity: cdk.Duration.days(3),
      },
      accountRecovery: cognito.AccountRecovery.EMAIL_ONLY,
      mfa: cognito.Mfa.OPTIONAL,
      mfaSecondFactor: { sms: false, otp: true },
      userVerification: {
        emailSubject: 'Verify your SentinelOps account',
        emailBody:
          'Welcome to SentinelOps. Your verification code is {####}.',
        emailStyle: cognito.VerificationEmailStyle.CODE,
      },
      removalPolicy: cdk.RemovalPolicy.RETAIN,
    });

    const userPoolClient = new cognito.UserPoolClient(this, 'SentinelOpsUserPoolClient', {
      userPool,
      userPoolClientName: 'sentinelops-api',
      generateSecret: false,
      authFlows: {
        userPassword: true,
      },
      preventUserExistenceErrors: true,
      accessTokenValidity: cdk.Duration.hours(1),
      idTokenValidity: cdk.Duration.hours(1),
      refreshTokenValidity: cdk.Duration.days(30),
    });

    new cdk.CfnOutput(this, 'UserPoolId', {
      value: userPool.userPoolId,
      description: 'Cognito User Pool ID — set as Cognito__UserPoolId on apps/api',
    });

    new cdk.CfnOutput(this, 'UserPoolClientId', {
      value: userPoolClient.userPoolClientId,
      description: 'Cognito User Pool Client ID — set as Cognito__ClientId on apps/api',
    });

    new cdk.CfnOutput(this, 'Region', {
      value: this.region,
      description: 'AWS region — set as Cognito__Region on apps/api',
    });

    const alertsQueueDlq = new sqs.Queue(this, 'AlertsQueueDlq', {
      queueName: 'sentinelops-alerts-dlq',
      retentionPeriod: cdk.Duration.days(14),
    });

    // Ingested alerts land here for apps/workers to pick up asynchronously.
    // Whichever construct ends up hosting the API's compute needs
    // `alertsQueue.grantSendMessages(...)`, and the workers' compute needs
    // `alertsQueue.grantConsumeMessages(...)` — neither exists yet in this stack.
    const alertsQueue = new sqs.Queue(this, 'AlertsQueue', {
      queueName: 'sentinelops-alerts',
      visibilityTimeout: cdk.Duration.seconds(60),
      retentionPeriod: cdk.Duration.days(4),
      deadLetterQueue: {
        queue: alertsQueueDlq,
        maxReceiveCount: 5,
      },
    });

    new cdk.CfnOutput(this, 'AlertsQueueUrl', {
      value: alertsQueue.queueUrl,
      description: 'SQS queue URL for ingested alerts — set as Aws__Sqs__AlertsQueueUrl on apps/api',
    });
  }
}
