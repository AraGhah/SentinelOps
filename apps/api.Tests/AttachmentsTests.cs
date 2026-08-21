using System.Net;
using System.Net.Http.Json;
using SentinelOps.Api.Attachments;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Incidents;
using static SentinelOps.Api.Tests.OrgTestHelpers;

namespace SentinelOps.Api.Tests;

[Collection("Api")]
public class AttachmentsTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task UploadUrl_Create_List_Delete_HappyPath()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Attachments Org");
        var incident = await CreateIncidentAsync(client, org.Id);

        var uploadUrlResponse = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/attachments/upload-url",
            new CreateUploadUrlRequest("screenshot.png", "image/png", 2048));
        uploadUrlResponse.EnsureSuccessStatusCode();
        var uploadUrl = await uploadUrlResponse.Content.ReadFromJsonAsync<UploadUrlResponse>(Json.Options);
        Assert.StartsWith($"orgs/{org.Id:N}/incidents/{incident.Id:N}/", uploadUrl!.StorageKey);

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/attachments",
            new CreateAttachmentRequest("screenshot.png", "image/png", 2048, uploadUrl.StorageKey));
        createResponse.EnsureSuccessStatusCode();
        var attachment = await createResponse.Content.ReadFromJsonAsync<AttachmentResponse>(Json.Options);
        Assert.Equal(AttachmentScanStatus.Pending, attachment!.ScanStatus);

        var listResponse = await client.GetFromJsonAsync<List<AttachmentResponse>>(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/attachments", Json.Options);
        Assert.Contains(listResponse!, a => a.Id == attachment.Id);

        var timelineResponse = await client.GetFromJsonAsync<List<IncidentEventResponse>>(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/timeline", Json.Options);
        Assert.Contains(timelineResponse!, e => e.EventType == IncidentEventType.AttachmentAdded);

        var deleteResponse = await client.DeleteAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/attachments/{attachment.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task CreateUploadUrl_RejectsDisallowedContentType()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Attachments Reject Org");
        var incident = await CreateIncidentAsync(client, org.Id);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/attachments/upload-url",
            new CreateUploadUrlRequest("payload.exe", "application/x-msdownload", 2048));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateUploadUrl_RejectsOversizedFile()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Attachments Size Org");
        var incident = await CreateIncidentAsync(client, org.Id);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/attachments/upload-url",
            new CreateUploadUrlRequest("huge.zip", "application/zip", AttachmentPolicy.MaxSizeBytes + 1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_UsesActualS3ObjectSize_NotClientClaimedSize()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Attachments Actual Size Org");
        var incident = await CreateIncidentAsync(client, org.Id);

        var uploadUrlResponse = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/attachments/upload-url",
            new CreateUploadUrlRequest("photo.png", "image/png", 2048));
        uploadUrlResponse.EnsureSuccessStatusCode();
        var uploadUrl = await uploadUrlResponse.Content.ReadFromJsonAsync<UploadUrlResponse>(Json.Options);

        // Simulate a client that actually uploaded more bytes than it later
        // claims in Create — the persisted record must reflect what S3 has,
        // not the client's SizeBytes.
        fixture.AttachmentStorage.SetObjectSize(uploadUrl!.StorageKey, 9999);

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/attachments",
            new CreateAttachmentRequest("photo.png", "image/png", 2048, uploadUrl.StorageKey));
        createResponse.EnsureSuccessStatusCode();
        var attachment = await createResponse.Content.ReadFromJsonAsync<AttachmentResponse>(Json.Options);

        Assert.Equal(9999, attachment!.SizeBytes);
    }

    [Fact]
    public async Task Create_RejectsWhenActualS3ObjectExceedsMaxSize()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Attachments Actual Oversize Org");
        var incident = await CreateIncidentAsync(client, org.Id);

        var uploadUrlResponse = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/attachments/upload-url",
            new CreateUploadUrlRequest("photo.png", "image/png", 2048));
        uploadUrlResponse.EnsureSuccessStatusCode();
        var uploadUrl = await uploadUrlResponse.Content.ReadFromJsonAsync<UploadUrlResponse>(Json.Options);

        // Client lied about SizeBytes at upload-url time — the actual object
        // it pushed to S3 blows the cap, and Create must catch this even
        // though the claimed SizeBytes here is within policy.
        fixture.AttachmentStorage.SetObjectSize(uploadUrl!.StorageKey, AttachmentPolicy.MaxSizeBytes + 1);

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/attachments",
            new CreateAttachmentRequest("photo.png", "image/png", 2048, uploadUrl.StorageKey));

        Assert.Equal(HttpStatusCode.BadRequest, createResponse.StatusCode);
    }

    [Fact]
    public async Task Create_RejectsStorageKeyFromAnotherIncident()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Attachments Ownership Org");
        var incidentA = await CreateIncidentAsync(client, org.Id);
        var incidentB = await CreateIncidentAsync(client, org.Id);

        var foreignKey = $"orgs/{org.Id:N}/incidents/{incidentB.Id:N}/{Guid.NewGuid():N}-file.png";

        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incidentA.Id}/attachments",
            new CreateAttachmentRequest("file.png", "image/png", 2048, foreignKey));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DownloadUrl_UnavailableUntilScanIsClean()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Attachments Scan Org");
        var incident = await CreateIncidentAsync(client, org.Id);

        var uploadUrlResponse = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/attachments/upload-url",
            new CreateUploadUrlRequest("report.pdf", "application/pdf", 2048));
        var uploadUrl = await uploadUrlResponse.Content.ReadFromJsonAsync<UploadUrlResponse>(Json.Options);

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/attachments",
            new CreateAttachmentRequest("report.pdf", "application/pdf", 2048, uploadUrl!.StorageKey));
        var attachment = await createResponse.Content.ReadFromJsonAsync<AttachmentResponse>(Json.Options);

        var downloadResponse = await client.GetAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/attachments/{attachment!.Id}/download-url");

        Assert.Equal(HttpStatusCode.Conflict, downloadResponse.StatusCode);
    }

    private static async Task<IncidentResponse> CreateIncidentAsync(HttpClient client, Guid orgId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{orgId}/incidents",
            new CreateIncidentRequest("Incident for attachment", null, IncidentSeverity.Medium, null, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IncidentResponse>(Json.Options))!;
    }
}
