using System.Text.Json;
using Microsoft.Graph;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions.Serialization;

namespace AccessReviewPortal.Services.Azure;

public static class GraphHelpers
{
    /// <summary>Reads all pages of a Graph collection response.</summary>
    public static async Task<List<TEntity>> ReadAllAsync<TEntity, TCollectionPage>(
        this GraphServiceClient graph, TCollectionPage? firstPage, CancellationToken ct, int maxItems = int.MaxValue)
        where TCollectionPage : IParsable, IAdditionalDataHolder, new()
    {
        var result = new List<TEntity>();
        if (firstPage is null)
        {
            return result;
        }

        var iterator = PageIterator<TEntity, TCollectionPage>.CreatePageIterator(graph, firstPage, item =>
        {
            result.Add(item);
            return result.Count < maxItems;
        });
        await iterator.IterateAsync(ct);
        return result;
    }

    public static int StatusCode(this Exception ex) => ex switch
    {
        ODataError o => o.ResponseStatusCode,
        Microsoft.Kiota.Abstractions.ApiException a => a.ResponseStatusCode,
        _ => 0
    };

    public static string ShortMessage(this Exception ex)
    {
        if (ex is ODataError o)
        {
            var msg = o.Error?.Message ?? o.Message;
            // PIM errors wrap a JSON payload in the message.
            if (msg.StartsWith('{'))
            {
                try
                {
                    using var doc = JsonDocument.Parse(msg);
                    if (doc.RootElement.TryGetProperty("message", out var m))
                    {
                        msg = m.GetString() ?? msg;
                    }
                }
                catch (JsonException)
                {
                }
            }
            return $"Graph {o.ResponseStatusCode} {o.Error?.Code}: {msg}";
        }
        return ex.Message;
    }
}
