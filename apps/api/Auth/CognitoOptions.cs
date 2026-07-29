namespace SentinelOps.Api.Auth;

public class CognitoOptions
{
    public const string SectionName = "Cognito";

    public required string Region { get; set; }
    public required string UserPoolId { get; set; }
    public required string ClientId { get; set; }

    public string Issuer => $"https://cognito-idp.{Region}.amazonaws.com/{UserPoolId}";
}
