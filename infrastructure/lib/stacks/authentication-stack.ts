import * as cdk from 'aws-cdk-lib/core';
import * as cognito from 'aws-cdk-lib/aws-cognito';
import { Construct } from 'constructs';
import { EnvironmentConfig } from '../config/environments';

export interface AuthenticationStackProps extends cdk.StackProps {
  config: EnvironmentConfig;
}

export class AuthenticationStack extends cdk.Stack {
  public readonly userPool: cognito.UserPool;
  public readonly userPoolClient: cognito.UserPoolClient;

  constructor(scope: Construct, id: string, props: AuthenticationStackProps) {
    super(scope, id, props);

    this.userPool = new cognito.UserPool(this, 'SentinelOpsUserPool', {
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
        emailBody: 'Welcome to SentinelOps. Your verification code is {####}.',
        emailStyle: cognito.VerificationEmailStyle.CODE,
      },
      // Always RETAIN regardless of environment (including dev) — unlike
      // Aurora/S3/DynamoDB, losing this invalidates every existing user's
      // account, which is disruptive even for a throwaway dev stack. Not
      // driven by config.removalPolicy.dataBearing on purpose.
      removalPolicy: cdk.RemovalPolicy.RETAIN,
    });

    this.userPoolClient = new cognito.UserPoolClient(this, 'SentinelOpsUserPoolClient', {
      userPool: this.userPool,
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
      value: this.userPool.userPoolId,
      description: 'Cognito User Pool ID — set as Cognito__UserPoolId on apps/api',
    });

    new cdk.CfnOutput(this, 'UserPoolClientId', {
      value: this.userPoolClient.userPoolClientId,
      description: 'Cognito User Pool Client ID — set as Cognito__ClientId on apps/api',
    });

    new cdk.CfnOutput(this, 'Region', {
      value: this.region,
      description: 'AWS region — set as Cognito__Region on apps/api',
    });
  }
}
