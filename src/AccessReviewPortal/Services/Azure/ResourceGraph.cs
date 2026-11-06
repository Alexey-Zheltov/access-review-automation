using System.Text.Json;
using global::Azure;
using global::Azure.ResourceManager.ResourceGraph;
using global::Azure.ResourceManager.ResourceGraph.Models;
using global::Azure.ResourceManager.Resources;

namespace AccessReviewPortal.Services.Azure;

/// <summary>Azure Resource Graph queries with the scanner identity (Reader is enough).</summary>
public static class ResourceGraph
{
    /// <summary>Runs a KQL query and follows skip tokens. <paramref name="subscriptions"/> empty = all visible subscriptions.</summary>
    public static async Task<List<JsonElement>> QueryAsync(AzureClients clients, string query, IReadOnlyCollection<string>? subscriptions, CancellationToken ct)
    {
        TenantResource? tenant = null;
        await foreach (var t in clients.Arm.GetTenants().GetAllAsync(ct))
        {
            tenant = t;
            break;
        }
        if (tenant is null) throw new RequestFailedException("No tenant is visible to the scanner identity.");

        var result = new List<JsonElement>();
        string? skipToken = null;
        do
        {
            var content = new ResourceQueryContent(query)
            {
                Options = new ResourceQueryRequestOptions { ResultFormat = ResultFormat.ObjectArray, Top = 1000, SkipToken = skipToken }
            };
            foreach (var s in subscriptions ?? []) content.Subscriptions.Add(s);

            var response = await tenant.GetResourcesAsync(content, ct);
            using var doc = JsonDocument.Parse(response.Value.Data.ToString());
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                result.AddRange(doc.RootElement.EnumerateArray().Select(e => e.Clone()));
            }
            skipToken = response.Value.SkipToken;
        } while (!string.IsNullOrEmpty(skipToken));
        return result;
    }
}
