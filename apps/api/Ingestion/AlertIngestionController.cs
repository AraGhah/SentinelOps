using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SentinelOps.Api.Domain;

namespace SentinelOps.Api.Ingestion;

[ApiController]
[Authorize(AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[EnableRateLimiting("ingestion")]
[Route("api/v1/integrations/{integrationId:guid}/alerts")]
public class AlertIngestionController(IAlertIngestionService ingestionService) : ControllerBase
{
    public const string TimestampHeader = "X-SentinelOps-Timestamp";
    public const string SignatureHeader = "X-SentinelOps-Signature";
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    private static readonly TimeSpan SignatureTolerance = TimeSpan.FromMinutes(5);

    [HttpPost]
    public async Task<IActionResult> Ingest(Guid integrationId, CancellationToken ct)
    {
        var integration = (Integration)HttpContext.Items[ApiKeyAuthenticationHandler.IntegrationContextKey]!;

        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var rawBody = await reader.ReadToEndAsync(ct);

        if (!Request.Headers.TryGetValue(TimestampHeader, out var timestampHeaderValues) ||
            !Request.Headers.TryGetValue(SignatureHeader, out var signatureHeaderValues))
        {
            return Problem(title: "Unauthorized", detail: "Missing signature headers.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var timestampHeader = timestampHeaderValues.ToString();
        if (!long.TryParse(timestampHeader, out var timestampUnixSeconds))
        {
            return Problem(title: "Unauthorized", detail: "Invalid timestamp header.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var signedAt = DateTimeOffset.FromUnixTimeSeconds(timestampUnixSeconds);
        if ((DateTimeOffset.UtcNow - signedAt).Duration() > SignatureTolerance)
        {
            // Older than the tolerance window in either direction: either a clock
            // problem or a replayed request presented long after it was signed.
            return Problem(title: "Unauthorized", detail: "Request timestamp is outside the allowed window.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var expectedSignature = ComputeSignature(integration.SigningSecret, timestampHeader, rawBody);
        var providedSignature = signatureHeaderValues.ToString();
        if (providedSignature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            providedSignature = providedSignature["sha256=".Length..];
        }

        if (!TryFixedTimeHexEquals(expectedSignature, providedSignature))
        {
            return Problem(title: "Unauthorized", detail: "Invalid request signature.", statusCode: StatusCodes.Status401Unauthorized);
        }

        IngestAlertRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<IngestAlertRequest>(
                rawBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            return Problem(title: "Invalid request", detail: $"Malformed JSON body: {ex.Message}", statusCode: StatusCodes.Status400BadRequest);
        }

        if (request is null)
        {
            return Problem(title: "Invalid request", detail: "Request body is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        var validationResults = new List<ValidationResult>();
        if (!Validator.TryValidateObject(request, new ValidationContext(request), validationResults, validateAllProperties: true))
        {
            foreach (var error in validationResults)
            {
                foreach (var member in error.MemberNames.DefaultIfEmpty(string.Empty))
                {
                    ModelState.AddModelError(member, error.ErrorMessage ?? "Invalid value.");
                }
            }

            return ValidationProblem(ModelState);
        }

        var timestampValidity = ingestionService.ValidateTimestamp(request.TimestampUtc);
        if (timestampValidity != TimestampValidity.Valid)
        {
            var detail = timestampValidity == TimestampValidity.TooFarInFuture
                ? "Alert timestamp is too far in the future."
                : "Alert timestamp is too old.";
            return Problem(title: "Invalid request", detail: detail, statusCode: StatusCodes.Status400BadRequest);
        }

        var idempotencyKey = Request.Headers.TryGetValue(IdempotencyKeyHeader, out var idempotencyValues) &&
            !string.IsNullOrWhiteSpace(idempotencyValues.ToString())
                ? idempotencyValues.ToString()
                : $"sig:{expectedSignature}";

        var result = await ingestionService.IngestAsync(
            integration.OrganizationId, integrationId, request, rawBody, idempotencyKey, ct);

        return StatusCode(StatusCodes.Status202Accepted, new IngestAlertResponse(
            result.AlertId, result.CorrelationId, result.WasDuplicate ? "duplicate" : "accepted"));
    }

    private static string ComputeSignature(string signingSecret, string timestampHeader, string rawBody)
    {
        var key = Encoding.UTF8.GetBytes(signingSecret);
        var message = Encoding.UTF8.GetBytes($"{timestampHeader}.{rawBody}");
        return Convert.ToHexString(HMACSHA256.HashData(key, message)).ToLowerInvariant();
    }

    private static bool TryFixedTimeHexEquals(string expectedHex, string providedHex)
    {
        try
        {
            var expected = Convert.FromHexString(expectedHex);
            var provided = Convert.FromHexString(providedHex);
            return expected.Length == provided.Length && CryptographicOperations.FixedTimeEquals(expected, provided);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
