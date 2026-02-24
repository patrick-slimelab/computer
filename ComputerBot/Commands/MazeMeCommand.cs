using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComputerBot.Abstractions;
using Matrix.Sdk.Core.Domain.RoomEvent;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ComputerBot.Commands
{
    public class MazeMeCommand : ICommand
    {
        public string Trigger => "!mazeme";
        private static readonly HttpClient _http = new HttpClient();

        private const int ImageSize = 1024;
        private const float Inset = 0f; // default requested

        private enum Dir { N = 0, E = 1, S = 2, W = 3 }
        private static readonly (int dx, int dy)[] Deltas = { (0, -1), (1, 0), (0, 1), (-1, 0) };
        private static Dir Opposite(Dir d) => (Dir)(((int)d + 2) % 4);

        public async Task ExecuteAsync(CommandContext ctx)
        {
            var prompt = ctx.Args?.Trim() ?? string.Empty;

            try
            {
                await ctx.Client.SendMessageAsync(ctx.RoomId, "`Generating maze...`");

                var mazeBytes = GenerateMazePng();

                if (string.IsNullOrWhiteSpace(prompt))
                {
                    var filename = $"mazeme_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.png";
                    await ctx.ImageRouter.SendImageWithRoutingAsync(ctx.Client, ctx.Db, ctx.RoomId, filename, mazeBytes);
                    return;
                }

                await ctx.Client.SendMessageAsync(ctx.RoomId, "`Running img2img on robokrabs...`");
                var stylized = await Img2ImgAsync(mazeBytes, prompt);
                var outName = $"mazeme_sd_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.png";
                await ctx.ImageRouter.SendImageWithRoutingAsync(ctx.Client, ctx.Db, ctx.RoomId, outName, stylized);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"MazeMe error: {ex}");
                await ctx.Client.SendMessageAsync(ctx.RoomId, $"`MazeMe error: {ex.Message}`");
            }
        }

        private static byte[] GenerateMazePng()
        {
            // Square-ish maze tuned for 1024 output
            const int widthCells = 48;
            const int heightCells = 48;
            var cellSize = ImageSize / (float)Math.Max(widthCells, heightCells);

            var open = new bool[widthCells, heightCells, 4];
            var visited = new bool[widthCells, heightCells];
            var rng = new Random();

            Carve(0, 0, visited, open, rng, widthCells, heightCells);

            using var img = new Image<Rgba32>(ImageSize, ImageSize, Color.Black);

            // Fill cells optional style hook: keep white for now.

            // Draw walls in black
            img.Mutate(ctx =>
            {
                for (int y = 0; y < heightCells; y++)
                {
                    for (int x = 0; x < widthCells; x++)
                    {
                        var x0 = x * cellSize;
                        var y0 = y * cellSize;
                        var x1 = (x + 1) * cellSize;
                        var y1 = (y + 1) * cellSize;

                        var insetPx = Inset * cellSize;
                        x0 += insetPx;
                        y0 += insetPx;
                        x1 -= insetPx;
                        y1 -= insetPx;

                        if (!open[x, y, (int)Dir.N]) ctx.DrawLine(Color.White, 3f, new PointF(x0, y0), new PointF(x1, y0));
                        if (!open[x, y, (int)Dir.E]) ctx.DrawLine(Color.White, 3f, new PointF(x1, y0), new PointF(x1, y1));
                        if (!open[x, y, (int)Dir.S]) ctx.DrawLine(Color.White, 3f, new PointF(x0, y1), new PointF(x1, y1));
                        if (!open[x, y, (int)Dir.W]) ctx.DrawLine(Color.White, 3f, new PointF(x0, y0), new PointF(x0, y1));
                    }
                }
            });

            using var ms = new MemoryStream();
            img.SaveAsPng(ms);
            return ms.ToArray();
        }

        private static void Carve(int x, int y, bool[,] visited, bool[,,] open, Random rng, int w, int h)
        {
            visited[x, y] = true;

            var dirs = new List<Dir> { Dir.N, Dir.E, Dir.S, Dir.W };
            for (int i = dirs.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (dirs[i], dirs[j]) = (dirs[j], dirs[i]);
            }

            foreach (var d in dirs)
            {
                var (dx, dy) = Deltas[(int)d];
                var nx = x + dx;
                var ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h || visited[nx, ny]) continue;

                open[x, y, (int)d] = true;
                open[nx, ny, (int)Opposite(d)] = true;
                Carve(nx, ny, visited, open, rng, w, h);
            }
        }

        private static string GetSdBaseUrl()
        {
            var baseUrl = Environment.GetEnvironmentVariable("SD_API_BASE");
            if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = "http://robokrabs:7860";
            return baseUrl.TrimEnd('/');
        }

        private static async Task<byte[]> Img2ImgAsync(byte[] initImage, string prompt)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

            // First try raw base64, then retry with data URL prefix for stricter backends.
            var raw = Convert.ToBase64String(initImage);
            var attempts = new[] { raw, $"data:image/png;base64,{raw}" };

            string? lastError = null;
            foreach (var init in attempts)
            {
                var payload = new
                {
                    init_images = new[] { init },
                    prompt,
                    steps = 20,
                    width = 1024,
                    height = 1024,
                    sampler_name = "Euler a",
                    denoising_strength = 0.75
                };

                var req = new HttpRequestMessage(HttpMethod.Post, $"{GetSdBaseUrl()}/sdapi/v1/img2img")
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };

                var auth = Environment.GetEnvironmentVariable("SD_AUTH");
                if (!string.IsNullOrWhiteSpace(auth))
                {
                    var bytes = Encoding.UTF8.GetBytes(auth);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(bytes));
                }

                var resp = await _http.SendAsync(req, cts.Token);
                var body = await resp.Content.ReadAsStringAsync(cts.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    lastError = $"HTTP {(int)resp.StatusCode}: {body}";
                    Console.WriteLine($"Img2img attempt failed: {lastError}");
                    continue;
                }

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("images", out var images) || images.GetArrayLength() == 0)
                    throw new Exception("No image returned from img2img");

                return Convert.FromBase64String(images[0].GetString() ?? throw new Exception("Empty img2img payload"));
            }

            throw new Exception($"img2img failed: {lastError ?? "unknown error"}");
        }
    }
}
