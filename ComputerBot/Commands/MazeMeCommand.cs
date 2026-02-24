using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private const int WideWidth = 1280;
        private const int WideHeight = 720;

        private static readonly Dictionary<string, string[]> Palettes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = new[] { "#000000" }, // pass-through (handled specially)
            ["synthwave"] = new[] { "#0B1026", "#2E1065", "#C026D3", "#22D3EE", "#FDE047" },
            ["forest"] = new[] { "#0B3D20", "#14532D", "#4D7C0F", "#84CC16", "#ECFCCB" },
            ["sunset"] = new[] { "#2D132C", "#801336", "#C72C41", "#EE4540", "#F7BE16" },
            ["ocean"] = new[] { "#081C2A", "#0E7490", "#06B6D4", "#67E8F9", "#E0F2FE" },
            ["mono"] = new[] { "#101010", "#333333", "#666666", "#BBBBBB", "#F5F5F5" }
        };

        public async Task ExecuteAsync(CommandContext ctx)
        {
            var rawArgs = ctx.Args?.Trim() ?? string.Empty;
            var isWide = false;
            var palette = "default";
            var prompt = rawArgs;

            if (!string.IsNullOrWhiteSpace(rawArgs) && rawArgs.Contains("--wide", StringComparison.OrdinalIgnoreCase))
            {
                isWide = true;
                prompt = prompt.Replace("--wide", "", StringComparison.OrdinalIgnoreCase).Trim();
            }

            foreach (var token in rawArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith("--palette=", StringComparison.OrdinalIgnoreCase) || token.StartsWith("--pallete=", StringComparison.OrdinalIgnoreCase))
                {
                    var v = token[(token.IndexOf('=') + 1)..].Trim();
                    if (!string.IsNullOrWhiteSpace(v)) palette = v;
                    prompt = prompt.Replace(token, "", StringComparison.OrdinalIgnoreCase).Trim();
                }
            }

            if (!Palettes.ContainsKey(palette))
            {
                var valid = string.Join(", ", Palettes.Keys.OrderBy(k => k));
                await ctx.Client.SendMessageAsync(ctx.RoomId, $"`Unknown palette '{palette}'. Use one of: {valid}`");
                return;
            }

            try
            {
                await ctx.Client.SendMessageAsync(ctx.RoomId, "`Generating maze...`");
                var mazeBytes = await GenerateMazeFromWasmAsync(isWide, palette);

                if (string.IsNullOrWhiteSpace(prompt))
                {
                    var filename = $"mazeme_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.png";
                    await ctx.ImageRouter.SendImageWithRoutingAsync(ctx.Client, ctx.Db, ctx.RoomId, filename, mazeBytes);
                    return;
                }

                await ctx.Client.SendMessageAsync(ctx.RoomId, "`Running img2img on robokrabs...`");
                var stylized = await Img2ImgAsync(mazeBytes, prompt, isWide);
                var outName = $"mazeme_sd_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.png";
                await ctx.ImageRouter.SendImageWithRoutingAsync(ctx.Client, ctx.Db, ctx.RoomId, outName, stylized);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"MazeMe error: {ex}");
                await ctx.Client.SendMessageAsync(ctx.RoomId, $"`MazeMe error: {ex.Message}`");
            }
        }

        private static async Task<byte[]> GenerateMazeFromWasmAsync(bool wide, string palette)
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
                Console.WriteLine($"MazeMe wasm fillStyle={s} parsed={fillColor}");
            }));

            linker.Define("env", "ctxStrokeStyle", Function.FromCallback(store, (Caller caller, int ptr, int len) =>
            {
                var s = ReadUtf8(caller, ptr, len);
                strokeColor = ParseCssColor(s, strokeColor);
                Console.WriteLine($"MazeMe wasm strokeStyle={s} parsed={strokeColor}");
            }));

            linker.Define("env", "ctxSetSize", Function.FromCallback(store, (int width, int height) =>
            {
                image?.Dispose();
                image = new Image<Rgba32>(Math.Max(1, width), Math.Max(1, height), Color.Black);
            }));

            linker.Define("env", "ctxFillRect", Function.FromCallback(store, (int x, int y, int w, int h) =>
            {
                if (image == null) return;
                image.Mutate(c => c.Fill(fillColor, new RectangleF(x, y, w, h)));
            }));

            linker.Define("env", "ctxFillAll", Function.FromCallback(store, () =>
            {
                if (image == null) return;
                image.Mutate(c => c.Fill(fillColor));
            }));

            // Important: preserve callback argument order exactly: (x1, x2, y1, y2)
            linker.Define("env", "ctxLine", Function.FromCallback(store, (int x1, int x2, int y1, int y2) =>
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
            setSeed.Invoke(Random.Shared.Next(1, int.MaxValue));
            var mazeW = wide ? 80 : 64;
            var mazeH = wide ? 45 : 64;
            var scale = 16;

            setWidth.Invoke(mazeW);
            setHeight.Invoke(mazeH);
            setScale.Invoke(scale);
            setInset.Invoke(0.0);
            gen.Invoke();

            if (image == null) throw new Exception("WASM did not initialize canvas");

            if (!palette.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                ApplyPalette(image, palette);
            }

            var targetW = wide ? WideWidth : TargetResolution;
            var targetH = wide ? WideHeight : TargetResolution;

            if (image.Width != targetW || image.Height != targetH)
            {
                image.Mutate(c => c.Resize(new ResizeOptions
                {
                    Size = new Size(targetW, targetH),
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
            s = s.Trim();

            // Common wasm output from mazeme: rgb(r,g,b)
            if (s.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase) && s.EndsWith(")"))
            {
                var inner = s.Substring(4, s.Length - 5);
                var parts = inner.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3 &&
                    byte.TryParse(parts[0], out var r) &&
                    byte.TryParse(parts[1], out var g) &&
                    byte.TryParse(parts[2], out var b))
                {
                    return Color.FromRgb(r, g, b);
                }
            }

            try
            {
                // Fallback for named/hex strings if present.
                return Color.Parse(s);
            }
            catch
            {
                return fallback;
            }
        }

        private static void ApplyPalette(Image<Rgba32> image, string paletteName)
        {
            if (!Palettes.TryGetValue(paletteName, out var hexes) || hexes.Length < 2) return;
            var anchors = hexes.Select(h => Color.ParseHex(h).ToPixel<Rgba32>()).ToArray();

            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        var p = row[x];

                        // Smooth luminance-based gradient remap to avoid chunky posterization.
                        var lum = (0.2126 * p.R + 0.7152 * p.G + 0.0722 * p.B) / 255.0;
                        var t = Math.Clamp(lum, 0.0, 1.0);

                        var scaled = t * (anchors.Length - 1);
                        var i0 = (int)Math.Floor(scaled);
                        var i1 = Math.Min(i0 + 1, anchors.Length - 1);
                        var local = scaled - i0;

                        var c0 = anchors[i0];
                        var c1 = anchors[i1];

                        byte Lerp(byte a, byte b, double tt) => (byte)Math.Clamp((int)Math.Round(a + (b - a) * tt), 0, 255);

                        row[x] = new Rgba32(
                            Lerp(c0.R, c1.R, local),
                            Lerp(c0.G, c1.G, local),
                            Lerp(c0.B, c1.B, local),
                            p.A);
                    }
                }
            });
        }

        private static string GetSdBaseUrl()
        {
            var baseUrl = Environment.GetEnvironmentVariable("SD_API_BASE");
            if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = "http://robokrabs:7860";
            return baseUrl.TrimEnd('/');
        }

        private static async Task<byte[]> Img2ImgAsync(byte[] initImage, string prompt, bool wide)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var outW = wide ? WideWidth : TargetResolution;
            var outH = wide ? WideHeight : TargetResolution;

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
                    width = outW,
                    height = outH,
                    sampler_name = "Euler a",
                    denoising_strength = 0.55
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
