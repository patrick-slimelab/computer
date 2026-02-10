using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Matrix.Sdk;

namespace ComputerBot.Services
{
    public class MatrixService
    {
        private readonly HttpClient _http = new HttpClient();
        private string _accessToken;
        private string _homeserverUrl;
        
        public IMatrixClient Client { get; }

        public MatrixService(IMatrixClient client)
        {
            Client = client;
        }

        public async Task LoginAsync(Uri hs, string user, string pass)
        {
            Console.WriteLine($"Logging in as {user} on {hs}...");
            var resp = await Client.LoginAsync(hs, user, pass, "computer-bot");
            _accessToken = resp.AccessToken;
            _homeserverUrl = hs.AbsoluteUri.TrimEnd('/');
            Client.Start();
        }

        public async Task<string> ResolveRoomId(string aliasOrId)
        {
            if (aliasOrId.StartsWith("!")) return aliasOrId;
            
            // 1. Try Directory API (GET) - safest read-only check
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
                            // Ensure joined
                            try { await Client.JoinTrustedPrivateRoomAsync(resolved); } catch {}
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

            // 2. Try Manual JOIN via v3 API (POST)
            try 
            {
                var joinUrl = $"{_homeserverUrl}/_matrix/client/v3/join/{Uri.EscapeDataString(aliasOrId)}";
                // Some servers require params, some empty object.
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
                        Console.WriteLine($"v3 Join success: {aliasOrId}");
                        return id.GetString();
                    }
                }
                Console.WriteLine($"v3 Join failed: {res.StatusCode} {body}");
            }
            catch (Exception ex) { Console.WriteLine($"v3 Join exception: {ex.Message}"); }

            // 3. Fallback to SDK (might be r0 or different logic)
            Console.WriteLine("Fallback to SDK Join...");
            var join = await Client.JoinTrustedPrivateRoomAsync(aliasOrId);
            return join.RoomId;
        }
    }
}
