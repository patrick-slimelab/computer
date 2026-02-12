using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Matrix.Sdk;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ComputerBot.Services
{
    public class MatrixService
    {
        private readonly HttpClient _http = new HttpClient();
        private readonly IMongoCollection<BsonDocument> _events;
        public string AccessToken { get; private set; }
        public string HomeserverUrl { get; private set; }
        
        public IMatrixClient Client { get; }

        public MatrixService(IMatrixClient client, IMongoCollection<BsonDocument> events)
        {
            Client = client;
            _events = events;
        }

        public async Task LoginAsync(Uri hs, string user, string pass)
        {
            Console.WriteLine($"Logging in as {user} on {hs}...");
            var resp = await Client.LoginAsync(hs, user, pass, "computer-bot");
            AccessToken = resp.AccessToken;
            HomeserverUrl = hs.AbsoluteUri.TrimEnd('/');
            Client.Start();
        }

        public async Task<byte[]> DownloadMxc(string mxcUrl)
        {
            if (string.IsNullOrEmpty(mxcUrl) || !mxcUrl.StartsWith("mxc://")) 
                throw new Exception($"Invalid MXC URL: {mxcUrl}");

            var parts = mxcUrl.Substring(6).Split('/');
            if (parts.Length < 2) throw new Exception($"Malformed MXC URL: {mxcUrl}");
            
            var server = parts[0];
            var mediaId = parts[1];
            
            var url = $"{HomeserverUrl}/_matrix/media/v3/download/{server}/{mediaId}";
            
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Authorization", $"Bearer {AccessToken}");
            
            var response = await _http.SendAsync(req);
            response.EnsureSuccessStatusCode();
            
            return await response.Content.ReadAsByteArrayAsync();
        }

        public async Task<string> ResolveRoomId(string aliasOrId)
        {
            if (aliasOrId.StartsWith("!")) return aliasOrId;
            
            // 1. Try Local MongoDB (Fastest, avoids API errors)
            if (aliasOrId.StartsWith("#"))
            {
                try 
                {
                    Console.WriteLine($"Resolving {aliasOrId} via MongoDB...");
                    
                    // Match m.room.canonical_alias
                    var canonical = await _events.Find(
                        Builders<BsonDocument>.Filter.Eq("content.alias", aliasOrId)
                    ).Project("{room_id: 1}").FirstOrDefaultAsync();

                    if (canonical != null && canonical.Contains("room_id"))
                    {
                        var id = canonical["room_id"].AsString;
                        Console.WriteLine($"Mongo resolved {aliasOrId} -> {id}");
                        return id;
                    }

                    // Match m.room.aliases list
                    var aliases = await _events.Find(
                        Builders<BsonDocument>.Filter.Eq("content.aliases", aliasOrId)
                    ).Project("{room_id: 1}").FirstOrDefaultAsync();

                    if (aliases != null && aliases.Contains("room_id"))
                    {
                        var id = aliases["room_id"].AsString;
                        Console.WriteLine($"Mongo resolved {aliasOrId} -> {id}");
                        return id;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Mongo lookup error: {ex.Message}");
                }
            }

            // 2. Try Directory API (GET)
            if (aliasOrId.StartsWith("#"))
            {
                try 
                {
                    var encoded = Uri.EscapeDataString(aliasOrId);
                    var url = $"{_homeserverUrl}/_matrix/client/v3/directory/room/{encoded}";
                    
                    var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Add("Authorization", $"Bearer {_accessToken}");
                    
                    var res = await _http.SendAsync(req);
                    if (res.IsSuccessStatusCode)
                    {
                        var json = await res.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("room_id", out var id))
                        {
                            var resolved = id.GetString();
                            Console.WriteLine($"Directory resolved {aliasOrId} -> {resolved}");
                            return resolved;
                        }
                    }
                    Console.WriteLine($"Directory lookup failed: {res.StatusCode}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Directory lookup exception: {ex.Message}");
                }
            }

            // 3. Fallback to Manual JOIN (v3 POST) - Last resort for unindexed rooms
            try 
            {
                var joinUrl = $"{_homeserverUrl}/_matrix/client/v3/join/{Uri.EscapeDataString(aliasOrId)}";
                var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
                var req = new HttpRequestMessage(HttpMethod.Post, joinUrl);
                req.Headers.Add("Authorization", $"Bearer {_accessToken}");
                req.Content = content;
                
                var res = await _http.SendAsync(req);
                var body = await res.Content.ReadAsStringAsync();
                
                if (res.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("room_id", out var id)) 
                    {
                        return id.GetString();
                    }
                }
                Console.WriteLine($"v3 Join failed: {res.StatusCode} {body}");
            }
            catch (Exception ex) { Console.WriteLine($"v3 Join exception: {ex.Message}"); }

            throw new Exception($"Could not resolve room alias: {aliasOrId}");
        }
    }
}
