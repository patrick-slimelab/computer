using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComputerBot.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Wasmtime;

namespace ComputerBot.Commands
{
    public class MazeMeCommand : ICommand
    {
        public string Trigger => "!mazeme";
        private static readonly HttpClient _http = new HttpClient();
        private const string WasmUrl = "https://dogspluspl.us/art/mazeme/zig-out/lib/masm.wasm";
        private const int TargetResolution = 1024;

        public async Task ExecuteAsync(CommandContext ctx)
        {
            var prompt = ctx.Args?.Trim() ?? string.Empty;

            try
            {
                await ctx.Client.SendMessageAsync(ctx.RoomId, "`Generating maze (spacedog wasm)...`");
                var mazeBytes = await GenerateMazeFromWasmAsync();

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

        private static async Task<byte[]> GenerateMazeFromWasmAsync()
        {
            var wasmPath = await EnsureWasmAsync();

            using var engine = new Engine();
            using var module = Module.FromFile(engine, wasmPath);
            using var store = new Store(engine);
            using var linker = new Linker(engine);

            Image<Rgba32>? image = null;
            Color fillColor = Color.Black;
            Color strokeColor = Color.White;

            linker.Define("env", "consoleDebug", Function.FromCallback(store, (int ptr, int len) =>
            {
                // no-op for now
            }));

            linker.Define("env", "ctxFillStyle", Function.FromCallback(store, (Caller caller, int ptr, int len) =>
            {
                var s = ReadUtf8(caller, ptr, len);
                fillColor = ParseCssColor(s, fillColor);
            }));

            linker.Define("env", "ctxStrokeStyle", Function.FromCallback(store, (Caller caller, int ptr, int len) =>
            {
                var s = ReadUtf8(caller, ptr, len);
                strokeColor = ParseCssColor(s, strokeColor);
            }));

            linker.Define("env", "ctxSetSize", Function.FromCallback(store, (int width, int height) =>
            {
                image?.Dispose();
                image = new Image<Rgba32>(Math.Max(1, width), Math.Max(1, height), Color.Black);
            }));

            linker.Define("env", "ctxFillRect", Function.FromCallback(store, (float x, float y, float w, float h) =>
            {
                if (image == null) return;
                image.Mutate(c => c.Fill(fillColor, new RectangleF(x, y, w, h)));
            }));

            linker.Define("env", "ctxFillAll", Function.FromCallback(store, () =>
            {
                if (image == null) return;
                image.Mutate(c => c.Fill(fillColor));
            }));

            // Important: preserve JS callback signature/order exactly: (x1, x2, y1, y2)
            linker.Define("env", "ctxLine", Function.FromCallback(store, (float x1, float x2, float y1, float y2) =>
            {
                if (image == null) return;
                image.Mutate(c => c.DrawLine(strokeColor, 1.5f, new PointF(x1, y1), new PointF(x2, y2)));
            }));

            var instance = linker.Instantiate(store, module);

            var setSeed = instance.GetFunction("setSeed");
            var setWidth = instance.GetFunction("setWidth");
            var setHeight = instance.GetFunction("setHeight");
            var setScale = instance.GetFunction("setScale");
            var setInset = instance.GetFunction("setInset");
            var gen = instance.GetFunction("gen");

            if (setSeed == null || setWidth == null || setHeight == null || setScale == null || setInset == null || gen == null)
                throw new Exception("mazeme wasm exports missing");

            // Match requested defaults
            setSeed.Invoke(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            setWidth.Invoke(64);
            setHeight.Invoke(64);
            setScale.Invoke(16);
            setInset.Invoke(0.0);
            gen.Invoke();

            if (image == null) throw new Exception("WASM did not initialize canvas");

            if (image.Width != TargetResolution || image.Height != TargetResolution)
            {
                image.Mutate(c => c.Resize(new ResizeOptions
                {
                    Size = new Size(TargetResolution, TargetResolution),
                    Mode = ResizeMode.Stretch,
                    Sampler = KnownResamplers.NearestNeighbor
                }));
            }

            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            image.Dispose();
            return ms.ToArray();
        }

        private static async Task<string> EnsureWasmAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), "mazeme_masm.wasm");
            if (File.Exists(path) && new FileInfo(path).Length > 1024) return path;
            var bytes = await _http.GetByteArrayAsync(WasmUrl);
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }

        private static string ReadUtf8(Caller caller, int ptr, int len)
        {
            var mem = caller.GetMemory("memory");
            if (mem == null || len <= 0) return string.Empty;
            if (ptr < 0 || len <= 0) return string.Empty;
            var data = mem.GetSpan<byte>(ptr);
            if (len > data.Length) return string.Empty;
            return Encoding.UTF8.GetString(data.Slice(0, len));
        }

        private static Color ParseCssColor(string s, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            s = s.Trim().ToLowerInvariant();

            return s switch
            {
                "black" => Color.Black,
                "white" => Color.White,
                "red" => Color.Red,
                "green" => Color.Green,
                "blue" => Color.Blue,
                "yellow" => Color.Yellow,
                "cyan" => Color.Cyan,
                "magenta" => Color.Magenta,
                _ => TryParseHex(s, fallback)
            };
        }

        private static Color TryParseHex(string s, Color fallback)
        {
            try
            {
                if (s.StartsWith("#")) return Color.ParseHex(s);
            }
            catch { }
            return fallback;
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
