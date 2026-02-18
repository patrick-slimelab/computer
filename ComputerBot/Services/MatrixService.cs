using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
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
        private readonly string _mediaUrlOverride;
        
        public string AccessToken { get; private set; } = string.Empty;
        public string HomeserverUrl { get; private set; } = string.Empty;
        
        public string MediaUrl
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_mediaUrlOverride) || 
                    _mediaUrlOverride.Equals("none", StringComparison.OrdinalIgnoreCase) || 
                    _mediaUrlOverride.Equals("empty", StringComparison.OrdinalIgnoreCase))
                {
                    return HomeserverUrl;
                }
                return _mediaUrlOverride.TrimEnd('/');
            }
        }
        
        public IMatrixClient Client { get; }

        public MatrixService(IMatrixClient client, IMongoCollection<BsonDocument> events)
        {
            Client = client;
            _events = events;
            _mediaUrlOverride = Environment.GetEnvironmentVariable("MATRIX_MEDIA_URL");
        }

        public async Task LoginAsync(Uri hs, string user, string pass)
        {
            var username = user;
            if (username.StartsWith("@") && username.Contains(":"))
            {
                username = username.Substring(1).Split(':')[0];
            }
            Console.WriteLine($"Logging in as {username} on {hs}...");
            var resp = await Client.LoginAsync(hs, username, pass, "computer-bot");
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
            
            var url = $"{MediaUrl}/_matrix/client/v1/media/download/{server}/{mediaId}?allow_redirect=true";
            Console.WriteLine($"Downloading media from: {url}");
            
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Authorization", $"Bearer {AccessToken}");
            req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            req.Headers.Add("Accept", "*/*");
            
            var response = await _http.SendAsync(req);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                throw new Exception($"HTTP {response.StatusCode}: {errorBody}");
            }
            
            return await response.Content.ReadAsByteArrayAsync();
        }

        public async Task AutoJoinPublicRoomsAsync(int pageLimit = 20)
        {
            if (string.IsNullOrWhiteSpace(HomeserverUrl) || string.IsNullOrWhiteSpace(AccessToken)) return;

            var joined = new HashSet<string>(StringComparer.Ordinal);
            var newlyJoined = 0;
            var discovered = 0;
            var nextBatch = string.Empty;

            try
            {
                var joinedReq = new HttpRequestMessage(HttpMethod.Get, $"{HomeserverUrl}/_matrix/client/v3/joined_rooms");
                joinedReq.Headers.Add("Authorization", $"Bearer {AccessToken}");
                var joinedRes = await _http.SendAsync(joinedReq);
                if (joinedRes.IsSuccessStatusCode)
                {
                    var joinedJson = await joinedRes.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(joinedJson);
                    if (doc.RootElement.TryGetProperty("joined_rooms", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in arr.EnumerateArray())
                        {
                            var rid = el.GetString();
                            if (!string.IsNullOrWhiteSpace(rid)) joined.Add(rid);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Auto-join: failed to fetch joined rooms: {ex.Message}");
            }

            Console.WriteLine($"Auto-join: starting public room scan (already joined: {joined.Count})");

            for (var page = 0; page < pageLimit; page++)
            {
                try
                {
                    var payload = string.IsNullOrEmpty(nextBatch)
                        ? "{}"
                        : JsonSerializer.Serialize(new { since = nextBatch });

                    var req = new HttpRequestMessage(HttpMethod.Post, $"{HomeserverUrl}/_matrix/client/v3/publicRooms");
                    req.Headers.Add("Authorization", $"Bearer {AccessToken}");
                    req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                    var res = await _http.SendAsync(req);
                    if (!res.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"Auto-join: publicRooms failed ({res.StatusCode})");
                        break;
                    }

                    var body = await res.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(body);

                    if (doc.RootElement.TryGetProperty("chunk", out var chunk) && chunk.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var room in chunk.EnumerateArray())
                        {
                            var joinRule = room.TryGetProperty("join_rule", out var jr) ? jr.GetString() : "public";
                            if (!string.Equals(joinRule, "public", StringComparison.OrdinalIgnoreCase)) continue;

                            var roomId = room.TryGetProperty("room_id", out var ridEl) ? ridEl.GetString() : null;
                            if (string.IsNullOrWhiteSpace(roomId)) continue;

                            discovered++;
                            if (joined.Contains(roomId)) continue;

                            try
                            {
                                await ResolveRoomId(roomId);
                                joined.Add(roomId);
                                newlyJoined++;
                                Console.WriteLine($"Auto-join: joined {roomId}");
                                await Task.Delay(200);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Auto-join: join failed for {roomId}: {ex.Message}");
                            }
                        }
                    }

                    if (doc.RootElement.TryGetProperty("next_batch", out var nb))
                    {
                        var next = nb.GetString();
                        if (string.IsNullOrWhiteSpace(next)) break;
                        nextBatch = next;
                    }
                    else
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Auto-join: page {page} failed: {ex.Message}");
                    break;
                }
            }

            Console.WriteLine($"Auto-join complete. Public discovered={discovered}, joined_now={newlyJoined}, total_joined={joined.Count}");
        }

        public async Task<string> ResolveRoomId(string aliasOrId)
        {
            if (aliasOrId.StartsWith("!"))
            {
                try
                {
                    var joinUrl = $"{HomeserverUrl}/_matrix/client/v3/rooms/{Uri.EscapeDataString(aliasOrId)}/join";
                    var req = new HttpRequestMessage(HttpMethod.Post, joinUrl);
                    req.Headers.Add("Authorization", $"Bearer {AccessToken}");
                    req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    var res = await _http.SendAsync(req);
                    if (!res.IsSuccessStatusCode)
                    {
                        var body = await res.Content.ReadAsStringAsync();
                        Console.WriteLine($"Join by roomId failed {aliasOrId}: {res.StatusCode} {body}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Join by roomId exception {aliasOrId}: {ex.Message}");
                }

                return aliasOrId;
            }
            
            // 1. Try Local MongoDB (Fastest, avoids API errors)
            if (aliasOrId.StartsWith("#"))
            {
                try 
                {
                    Console.WriteLine($"Resolving {aliasOrId} via MongoDB...");
                    
                    var canonical = await _events.Find(
                        Builders<BsonDocument>.Filter.Eq("content.alias", aliasOrId)
                    ).Project("{room_id: 1}").FirstOrDefaultAsync();

                    if (canonical != null && canonical.Contains("room_id"))
                    {
                        var id = canonical["room_id"].AsString;
                        Console.WriteLine($"Mongo resolved {aliasOrId} -> {id}");
                        return id;
                    }

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
                    var url = $"{HomeserverUrl}/_matrix/client/v3/directory/room/{encoded}";
                    
                    var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Add("Authorization", $"Bearer {AccessToken}");
                    
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

            // 3. Fallback to Manual JOIN (v3 POST)
            try 
            {
                var joinUrl = $"{HomeserverUrl}/_matrix/client/v3/join/{Uri.EscapeDataString(aliasOrId)}";
                var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
                var req = new HttpRequestMessage(HttpMethod.Post, joinUrl);
                req.Headers.Add("Authorization", $"Bearer {AccessToken}");
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
