using System.Text.Json;
using AccessReviewPortal.Options;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Microsoft.Extensions.Options;
using Microsoft.Graph;

namespace AccessReviewPortal.Services.Azure;

/// <summary>
/// One credential for ARM, Graph and Log Analytics. Client secret (service principal) when configured,
/// otherwise DefaultAzureCredential (managed identity, Azure CLI, etc.).
/// </summary>
public sealed class AzureClients
{
    public const string ArmScope = "https://management.azure.com/.default";
    public const string GraphScope = "https://graph.microsoft.com/.default";

    public AzureClients(IOptions<AzureAdOptions> options)
    {
        var o = options.Value;
        TenantId = string.IsNullOrWhiteSpace(o.TenantId) ? null : o.TenantId;
        ClientId = string.IsNullOrWhiteSpace(o.ClientId) ? null : o.ClientId;

        if (o.HasClientSecret)
        {
            Credential = new ClientSecretCredential(o.TenantId, o.ClientId, o.ClientSecret);
            AuthMode = $"Service principal (client secret), appId {o.ClientId}";
        }
        else
        {
            Credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions { TenantId = TenantId });
            AuthMode = "DefaultAzureCredential (no client secret configured)";
        }

        Arm = new ArmClient(Credential);

        // Audit and sign-in queries can take more than the default 100 seconds per page.
        var graphHttpClient = GraphClientFactory.Create();
        graphHttpClient.Timeout = TimeSpan.FromMinutes(3);
        Graph = new GraphServiceClient(graphHttpClient, Credential, [GraphScope]);
    }

    public TokenCredential Credential { get; }
    public ArmClient Arm { get; }
    public GraphServiceClient Graph { get; }
    public string AuthMode { get; }
    public string? TenantId { get; }
    public string? ClientId { get; }

    public async Task<AccessToken> GetTokenAsync(string scope, CancellationToken ct) =>
        await Credential.GetTokenAsync(new TokenRequestContext([scope], tenantId: TenantId), ct);

    /// <summary>Reads application permissions ("roles" claim) and identity from an access token.</summary>
    public static TokenInfo DescribeToken(string accessToken)
    {
        var parts = accessToken.Split('.');
        var payload = parts.Length > 1 ? parts[1].Replace('-', '+').Replace('_', '/') : "";
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
        var root = doc.RootElement;

        string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var roles = root.TryGetProperty("roles", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(x => x.GetString() ?? "").OrderBy(x => x).ToList()
            : [];
        var exp = root.TryGetProperty("exp", out var e) && e.TryGetInt64(out var secs)
            ? DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime
            : DateTime.MinValue;

        return new TokenInfo(
            Roles: roles,
            Scopes: (Str("scp") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(),
            ObjectId: Str("oid"),
            AppId: Str("appid") ?? Str("azp"),
            TenantId: Str("tid"),
            Upn: Str("upn") ?? Str("unique_name"),
            ExpiresOnUtc: exp);
    }
}

public sealed record TokenInfo(
    List<string> Roles,
    List<string> Scopes,
    string? ObjectId,
    string? AppId,
    string? TenantId,
    string? Upn,
    DateTime ExpiresOnUtc)
{
    public bool IsAppOnly => Scopes.Count == 0;
}
