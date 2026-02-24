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
            // these act as seed hues; final palette is generated semi-randomly each run
            ["spacedog"] = new[] { "#7c3aed" },
            ["synthwave"] = new[] { "#c026d3" },
            ["forest"] = new[] { "#22c55e" },
            ["sunset"] = new[] { "#f97316" },
            ["ocean"] = new[] { "#06b6d4" },
            ["mono"] = new[] { "#64748b" }
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
            var setBraid = instance.GetFunction("setBraid");
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
            // Slightly higher openness (braid) so there are fewer dead-ends.
            setBraid?.Invoke(0.12);
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
            var anchors = BuildDynamicPalette(paletteName);
            if (anchors.Length < 2) return;

            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        var p = row[x];
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

        private static Rgba32[] BuildDynamicPalette(string paletteName)
        {
            if (paletteName.Equals("synthwave", StringComparison.OrdinalIgnoreCase))
            {
                // Extra-neon synthwave lane (with slight per-run variation)
                string[][] neonSets =
                {
                    new[] { "#05030A", "#19062F", "#4E08A8", "#FF00CC", "#00E5FF", "#F5FF3B" },
                    new[] { "#08040F", "#22093A", "#6510C9", "#FF2BD6", "#33F0FF", "#EFFF66" },
                    new[] { "#03020A", "#14052B", "#5A00B8", "#FF00A8", "#00D8FF", "#DBFF4A" }
                };

                var pick = neonSets[Random.Shared.Next(neonSets.Length)];
                return pick.Select(Color.ParseHex).Select(c => c.ToPixel<Rgba32>()).ToArray();
            }

            // 10x10 matrix: rows are "roles" in the palette (dark bg -> bright accent),
            // columns are coherent style variants. We pick (with jitter) a column sample per row.
            string[][] matrix =
            {
                new[] {"#07070A","#0A0A12","#081018","#101015","#0B1110","#120C09","#0D0A13","#0A1216","#121212","#0A0E0D"},
                new[] {"#141423","#1A1630","#122335","#24222E","#173026","#311D14","#241A35","#173138","#2A2A2A","#193029"},
                new[] {"#23233D","#2D2452","#1B3A56","#3B354A","#1F513D","#4A2D1F","#3A2B57","#215061","#434343","#275147"},
                new[] {"#3A3A66","#43337B","#2A5C78","#5A4A72","#2C7A59","#6E4730","#563F84","#31728B","#646464","#3C7668"},
                new[] {"#5656A0","#5F49B0","#3C83A4","#8A6FB0","#43A06C","#A56A42","#7C60BE","#4598B8","#878787","#53A08B"},
                new[] {"#7878C8","#8468D2","#58A7C8","#B18DCE","#63C28C","#C98C59","#A182D8","#63BED6","#AAAAAA","#72C2A9"},
                new[] {"#9B9BE6","#A08BEA","#79C5E1","#D1B0E3","#8BD9AE","#E1AE83","#C2A6EA","#8AD6E9","#C8C8C8","#98D9C5"},
                new[] {"#BDBDF4","#C0B0F4","#A0DBEE","#E6CCE9","#B1E9CC","#ECC7AA","#D9C8F4","#B2E8F2","#DEDEDE","#BCE9DA"},
                new[] {"#DDDDFB","#DED3FA","#C9EEF8","#F3E2F5","#D2F4E4","#F6DFC6","#EADFF9","#D3F3F8","#F0F0F0","#DCF4EA"},
                new[] {"#F5F5FF","#F4F0FF","#EAF8FF","#FDF3FD","#ECFBF3","#FFF3E9","#F8F3FF","#EDF9FF","#FAFAFA","#EFFAF5"}
            };

            int baseCol = paletteName.ToLowerInvariant() switch
            {
                "synthwave" => 1,
                "ocean" => 2,
                "mono" => 8,
                "forest" => 4,
                "sunset" => 5,
                "spacedog" => 7,
                _ => Random.Shared.Next(0, 10)
            };

            var colors = new Rgba32[matrix.Length];
            for (int row = 0; row < matrix.Length; row++)
            {
                // choose nearby column per row for semi-random contrast while keeping cohesion
                int jitter = Random.Shared.Next(-1, 2);
                int col = (baseCol + jitter + 10) % 10;
                colors[row] = Color.ParseHex(matrix[row][col]).ToPixel<Rgba32>();
            }

            return colors;
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
