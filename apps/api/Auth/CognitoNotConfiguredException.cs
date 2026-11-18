namespace SentinelOps.Api.Auth;

// Thrown instead of letting the AWS SDK fail with an opaque error (a blank region
// produces "UriFormatException: The hostname could not be parsed") when the API is
// started without Cognito settings. The message is for operators/logs; the client
// gets the neutral 503 from CognitoExceptionMapper.
public class CognitoNotConfiguredException() : InvalidOperationException(
    "Cognito is not configured, so sign-up and sign-in cannot work. Set Cognito__Region, " +
    "Cognito__UserPoolId and Cognito__ClientId on the API (the Region, UserPoolId and " +
    "UserPoolClientId outputs of the Authentication CDK stack; see .env.example).");
