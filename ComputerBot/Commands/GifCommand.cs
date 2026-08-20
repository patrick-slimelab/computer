using System.Text.Json;
using ComputerBot.Abstractions;

namespace ComputerBot.Commands;

public sealed class GifCommand : ICommand
{
    private static readonly HttpClient Http = CreateHttpClient();

    public string Trigger => "!gif";

    public async Task ExecuteAsync(CommandContext ctx)
    {
        var query = ctx.Args.Trim();
        if (query.Length == 0)
        {
            await ctx.Client.SendMessageAsync(ctx.RoomId, "`Usage: !gif <query>`");
            return;
        }

        var apiKey = Environment.GetEnvironmentVariable("KLIPY_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            await ctx.Client.SendMessageAsync(ctx.RoomId, "`GIF search is not configured: missing KLIPY_API_KEY.`");
            return;
        }

        var gifBytes = await DownloadBestMatchAsync(apiKey, query);
        var filename = $"klipy_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.gif";
        await ctx.ImageRouter.SendImageWithRoutingAsync(ctx.Client, ctx.Db, ctx.RoomId, filename, gifBytes);
    }

    private static async Task<byte[]> DownloadBestMatchAsync(string apiKey, string query)
    {
        var endpoint = $"https://api.klipy.com/v2/search?q={Uri.EscapeDataString(query)}&key={Uri.EscapeDataString(apiKey)}&limit=1";
        using var response = await Http.GetAsync(endpoint);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new Exception($"KLIPY search failed ({(int)response.StatusCode})");

        using var document = JsonDocument.Parse(json);
        var gifUrl = FindBestGifUrl(document.RootElement);
        if (gifUrl is null) throw new Exception("No GIFs found for that query");

        using var gifResponse = await Http.GetAsync(gifUrl);
        gifResponse.EnsureSuccessStatusCode();
        return await gifResponse.Content.ReadAsByteArrayAsync();
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ComputerBot/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json,image/gif,*/*");
        return client;
    }

    private static string? FindBestGifUrl(JsonElement root)
    {
        var candidates = new List<(int Score, string Url)>();
        CollectUrls(root, "", candidates);
        return candidates.OrderByDescending(x => x.Score).Select(x => x.Url).FirstOrDefault();
    }

    private static void CollectUrls(JsonElement node, string path, List<(int Score, string Url)> urls)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
                CollectUrls(property.Value, $"{path}/{property.Name.ToLowerInvariant()}", urls);
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            // The API ranks its first result as the best match, so only inspect it.
            if (node.GetArrayLength() > 0) CollectUrls(node[0], $"{path}/0", urls);
        }
        else if (node.ValueKind == JsonValueKind.String && path.EndsWith("/url"))
        {
            var value = node.GetString();
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
            var score = path.Contains("gif") ? 100 : 0;
            if (path.Contains("hd")) score += 20;
            if (path.Contains("preview") || path.Contains("tiny") || path.Contains("nano")) score -= 30;
            if (uri.AbsolutePath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) score += 50;
            if (score > 0) urls.Add((score, value!));
        }
    }

}
