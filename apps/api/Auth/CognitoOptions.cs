namespace SentinelOps.Api.Auth;

public class CognitoOptions
{
    public const string SectionName = "Cognito";

    public required string Region { get; set; }
    public required string UserPoolId { get; set; }
    public required string ClientId { get; set; }

    public string Issuer => $"https://cognito-idp.{Region}.amazonaws.com/{UserPoolId}";

    // The repo ships these blank (appsettings.json, .env.example); real values come from
    // the Authentication CDK stack outputs. Blank values make every Cognito call fail.
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Region)
        && !string.IsNullOrWhiteSpace(UserPoolId)
        && !string.IsNullOrWhiteSpace(ClientId);
}
