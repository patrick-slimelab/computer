using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ComputerBot.Abstractions;

namespace ComputerBot.Commands
{
    public class DongeonCommand : ICommand
    {
        public string Trigger => "!dongeon";

        private static readonly HttpClient Http = new HttpClient();
        private static readonly Regex CClubMxid = new Regex(@"^@[a-z0-9._=/+-]+:cclub\.cs\.wmich\.edu$", RegexOptions.Compiled);

        public async Task ExecuteAsync(CommandContext ctx)
        {
            var args = (ctx.Args ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(args) || args.Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                await ctx.Client.SendMessageAsync(ctx.RoomId,
                    "`Usage: !dongeon login`\n" +
                    "`login` creates your Dongeon account from your real CClub Matrix sender ID if needed, then returns a short-lived one-use browser login link.");
                return;
            }

            var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var subcommand = parts[0].ToLowerInvariant();
            var rest = parts.Length > 1 ? parts[1].Trim() : string.Empty;

            // Security boundary: this must be the Matrix event sender MXID from the homeserver,
            // never a display name, Discord name, room nickname, or user-submitted alias.
            var sender = ctx.SenderId?.Trim() ?? string.Empty;
            if (!CClubMxid.IsMatch(sender))
            {
                await ctx.Client.SendMessageAsync(ctx.RoomId, "`The Dongeon only accepts canonical CClub Matrix IDs like @shaggy:cclub.cs.wmich.edu.`");
                return;
            }

            if (subcommand == "create")
            {
                await ctx.Client.SendMessageAsync(ctx.RoomId, "`!dongeon create is no longer needed. Use !dongeon login and I will create your account if needed.`");
                return;
            }

            if (subcommand == "login")
            {
                await IssueLogin(ctx, sender);
                return;
            }

            // Legacy verifier kept temporarily for old generated pairing codes, but new users
            // should not need it because browser-origin account creation is disabled.
            if (subcommand == "verify")
            {
                if (string.IsNullOrWhiteSpace(rest))
                {
                    await ctx.Client.SendMessageAsync(ctx.RoomId, "`Usage: !dongeon verify <pairing-code>`");
                    return;
                }
                await VerifyLegacyPairing(ctx, sender, rest);
                return;
            }

            await ctx.Client.SendMessageAsync(ctx.RoomId, "`Unknown !dongeon command. Usage: !dongeon login`");
        }

        private static string VerifyUrl(string path)
        {
            var baseUrl = Environment.GetEnvironmentVariable("EVENNIA_VERIFY_URL") ?? "http://patrick:14001/auth/cclub/verify-pairing";
            if (baseUrl.EndsWith("/verify-pairing", StringComparison.OrdinalIgnoreCase))
            {
                baseUrl = baseUrl[..^"/verify-pairing".Length];
            }
            return baseUrl.TrimEnd('/') + path;
        }

        private static string? VerifyToken() => Environment.GetEnvironmentVariable("EVENNIA_VERIFY_TOKEN");

        private static async Task<JsonElement?> PostJson(CommandContext ctx, string path, object payload)
        {
            var verifyToken = VerifyToken();
            if (string.IsNullOrWhiteSpace(verifyToken))
            {
                await ctx.Client.SendMessageAsync(ctx.RoomId, "`Dongeon verifier is not configured: missing EVENNIA_VERIFY_TOKEN.`");
                return null;
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, VerifyUrl(path));
            req.Headers.Add("X-Evennia-Verifier-Token", verifyToken);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var res = await Http.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement.Clone();

            if (!res.IsSuccessStatusCode || !root.TryGetProperty("ok", out var okEl) || !okEl.GetBoolean())
            {
                var error = root.TryGetProperty("error", out var errEl) ? errEl.GetString() : $"HTTP {(int)res.StatusCode}";
                await ctx.Client.SendMessageAsync(ctx.RoomId, $"`Dongeon request failed: {error}`");
                return null;
            }

            return root;
        }

        private static async Task ProvisionAccount(CommandContext ctx, string sender)
        {
            try
            {
                var root = await PostJson(ctx, "/provision-account", new
                {
                    matrix_id = sender,
                    display_name = sender.TrimStart('@').Split(':')[0]
                });
                if (root is null) return;

                var account = root.Value.TryGetProperty("account", out var accountEl) ? accountEl.GetString() : "your account";
                var created = root.Value.TryGetProperty("created_account", out var createdEl) && createdEl.GetBoolean();
                var verb = created ? "created and linked" : "already linked";
                await ctx.Client.SendMessageAsync(ctx.RoomId, $"✅ Dongeon account `{account}` {verb} for `{sender}`. Use `!dongeon login` for a one-use webclient link.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Dongeon provision error: {ex}");
                await ctx.Client.SendMessageAsync(ctx.RoomId, $"`Dongeon provision error: {ex.Message}`");
            }
        }

        private static async Task IssueLogin(CommandContext ctx, string sender)
        {
            try
            {
                // Login is the only user-facing flow now. Provision/link first so a
                // first-time CClub Matrix user can simply run `!dongeon login`.
                var provision = await PostJson(ctx, "/provision-account", new
                {
                    matrix_id = sender,
                    display_name = sender.TrimStart('@').Split(':')[0]
                });
                if (provision is null) return;

                var root = await PostJson(ctx, "/issue-login-token", new { matrix_id = sender, minutes = 15 });
                if (root is null) return;

                var path = root.Value.GetProperty("login_path").GetString() ?? "";
                var urlBase = Environment.GetEnvironmentVariable("EVENNIA_PUBLIC_URL") ?? "https://the-dongeon.scoob.dog";
                var url = urlBase.TrimEnd('/') + path;
                var dmRoomId = await ctx.MatrixService.GetOrCreateDirectRoomAsync(sender);
                await ctx.Client.SendMessageAsync(dmRoomId, $"🔐 One-use Dongeon login link for `{sender}`; expires in 15 minutes:\n{url}\n`Do not share this link; it is a bearer token and works once.`");
                await ctx.Client.SendMessageAsync(ctx.RoomId, $"🔐 The computer hums and delivers a sealed one-use Dongeon access sigil to `{sender}`'s private terminal.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Dongeon login-token error: {ex}");
                await ctx.Client.SendMessageAsync(ctx.RoomId, $"`Dongeon login-token error: {ex.Message}`");
            }
        }

        private static async Task VerifyLegacyPairing(CommandContext ctx, string sender, string code)
        {
            try
            {
                var root = await PostJson(ctx, "/verify-pairing", new
                {
                    matrix_id = sender,
                    code,
                    display_name = sender.TrimStart('@').Split(':')[0]
                });
                if (root is null) return;

                var account = root.Value.TryGetProperty("account", out var accountEl) ? accountEl.GetString() : "your account";
                await ctx.Client.SendMessageAsync(ctx.RoomId, $"✅ Dongeon account `{account}` linked for `{sender}`. New flow: use `!dongeon login`.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Dongeon verifier error: {ex}");
                await ctx.Client.SendMessageAsync(ctx.RoomId, $"`Dongeon verifier error: {ex.Message}`");
            }
        }
    }
}
