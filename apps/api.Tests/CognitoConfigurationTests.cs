using Microsoft.Extensions.Options;
using SentinelOps.Api.Auth;

namespace SentinelOps.Api.Tests;

// Regression: with blank Cognito settings (the repo default) the AWS SDK failed every
// sign-up with "UriFormatException: The hostname could not be parsed", which reached the
// user as a generic "Please try again." and was never logged.
public class CognitoConfigurationTests
{
    private static CognitoAuthService Create(string region, string userPoolId, string clientId) =>
        new(Options.Create(new CognitoOptions { Region = region, UserPoolId = userPoolId, ClientId = clientId }));

    [Theory]
    [InlineData("", "", "")]
    [InlineData("us-east-1", "", "")]
    [InlineData("us-east-1", "pool", "")]
    [InlineData("", "pool", "client")]
    [InlineData("   ", "pool", "client")]
    public void Constructor_AnySettingBlank_ThrowsNotConfigured(string region, string userPoolId, string clientId)
    {
        Assert.Throws<CognitoNotConfiguredException>(() => Create(region, userPoolId, clientId));
    }

    [Fact]
    public void Constructor_AllSettingsPresent_DoesNotThrow()
    {
        var service = Create("us-east-1", "us-east-1_example", "example-client-id");

        Assert.NotNull(service);
    }

    [Fact]
    public void NotConfigured_MapsTo503_WithoutLeakingSettingNames()
    {
        var problem = new CognitoNotConfiguredException().ToProblemDetails();

        Assert.Equal(503, problem.Status);
        Assert.DoesNotContain("Cognito__", problem.Detail);
    }
}
