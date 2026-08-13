import * as cdk from 'aws-cdk-lib/core';
import { Template } from 'aws-cdk-lib/assertions';
import { AuthenticationStack } from '../lib/stacks/authentication-stack';
import { environments } from '../lib/config/environments';

function synth(): Template {
  const app = new cdk.App();
  const stack = new AuthenticationStack(app, 'TestAuthenticationStack', {
    config: environments.dev,
  });
  return Template.fromStack(stack);
}

describe('AuthenticationStack', () => {
  it('creates a Cognito User Pool with MFA optional and a password policy', () => {
    const template = synth();
    template.resourceCountIs('AWS::Cognito::UserPool', 1);
    template.hasResourceProperties('AWS::Cognito::UserPool', {
      UserPoolName: 'sentinelops-users',
      Policies: { PasswordPolicy: { MinimumLength: 8, RequireSymbols: true } },
    });
  });

  it('always retains the User Pool, even for the dev environment', () => {
    const template = synth();
    template.hasResource('AWS::Cognito::UserPool', { DeletionPolicy: 'Retain' });
  });

  it('creates a User Pool Client with no client secret (public SPA/API caller)', () => {
    const template = synth();
    template.hasResourceProperties('AWS::Cognito::UserPoolClient', {
      ClientName: 'sentinelops-api',
      GenerateSecret: false,
    });
  });
});
