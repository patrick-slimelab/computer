using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ComputerBot.Abstractions;

using System.Net.Http.Headers;
using System.Threading;
using Matrix.Sdk.Core.Domain.RoomEvent;

namespace ComputerBot.Commands
{
    public class StableDiffusionCommand : ICommand
    {
        public virtual string Trigger => "!sd";
        private static readonly HttpClient _http = new HttpClient();

        protected virtual string GetSdBaseUrl()
        {
            var baseUrl = Environment.GetEnvironmentVariable("SD_API_BASE");
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                baseUrl = "http://robokrabs:7860";
            }
            return baseUrl.TrimEnd('/');
        }

        protected virtual bool UseComfyBackend(string baseUrl)
        {
            var backend = Environment.GetEnvironmentVariable("SD_BACKEND");
            if (!string.IsNullOrWhiteSpace(backend) && backend.Equals("comfy", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return baseUrl.Contains(":8188");
        }

        public async Task ExecuteAsync(CommandContext ctx)
        {
            var prompt = ctx.Args?.Trim();
            if (string.IsNullOrEmpty(prompt))
            {
                await ctx.Client.SendMessageAsync(ctx.RoomId, $"`Usage: {Trigger} <prompt>`");
                return;
            }

            Console.WriteLine($"SD Prompt: {prompt}");
            await ctx.Client.SendMessageAsync(ctx.RoomId, "`Generating...`");

            try
            {
                var baseUrl = GetSdBaseUrl();
                if (UseComfyBackend(baseUrl))
                {
                    await ExecuteComfyAsync(ctx, prompt, baseUrl);
                    return;
                }

                await ExecuteA1111Async(ctx, prompt, baseUrl);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SD Error: {ex}");
                await ctx.Client.SendMessageAsync(ctx.RoomId, $"`Error generating image: {ex.Message}`");
            }
        }

        private async Task ExecuteA1111Async(CommandContext ctx, string prompt, string baseUrl)
        {
            string endpoint = $"{baseUrl}/sdapi/v1/txt2img";
            object payload = null;

            // Check for img2img
            if (!string.IsNullOrEmpty(ctx.ReplyToEventId))
            {
                Console.WriteLine($"Processing reply to {ctx.ReplyToEventId}");
                try
                {
                    var replyEvent = await ctx.Client.GetEvent(ctx.ReplyToEventId);
                    Console.WriteLine($"Reply event is {replyEvent?.GetType().Name}");

                    if (replyEvent is ImageMessageEvent imgEvent)
                    {
                        Console.WriteLine($"Downloading {imgEvent.MxcUrl}");
                        await ctx.Client.SendMessageAsync(ctx.RoomId, "`Found image in reply, using img2img...`");
                        var bytes = await ctx.MatrixService.DownloadMxc(imgEvent.MxcUrl);
                        var base64 = Convert.ToBase64String(bytes);

                        endpoint = $"{baseUrl}/sdapi/v1/img2img";
                        payload = new
                        {
                            init_images = new[] { base64 },
                            prompt = prompt,
                            steps = 20,
                            width = 1024,
                            height = 1024,
                            sampler_name = "Euler a",
                            denoising_strength = 0.75
                        };
                    }
                    else
                    {
                        Console.WriteLine("Reply event is not an image.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to fetch reply image: {ex.Message}");
                    await ctx.Client.SendMessageAsync(ctx.RoomId, "`Warning: Could not download reply image. Falling back to text-to-image...`");
                }
            }

            if (payload == null)
            {
                payload = new
                {
                    prompt = prompt,
                    steps = 20,
                    width = 1024,
                    height = 1024,
                    sampler_name = "Euler a"
                };
            }

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Content = content;

            var auth = Environment.GetEnvironmentVariable("SD_AUTH");
            if (!string.IsNullOrEmpty(auth))
            {
                var bytes = Encoding.UTF8.GetBytes(auth);
                var header = Convert.ToBase64String(bytes);
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", header);
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

            var response = await _http.SendAsync(request, cts.Token);
            response.EnsureSuccessStatusCode();

            var respJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(respJson);

            if (doc.RootElement.TryGetProperty("images", out var images) && images.GetArrayLength() > 0)
            {
                var base64 = images[0].GetString();
                var bytes = Convert.FromBase64String(base64);

                var filename = $"sd_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.png";
                await ctx.ImageRouter.SendImageWithRoutingAsync(ctx.Client, ctx.Db, ctx.RoomId, filename, bytes);
            }
            else
            {
                await ctx.Client.SendMessageAsync(ctx.RoomId, "`Error: No image returned from SD API`");
            }
        }

        private async Task ExecuteComfyAsync(CommandContext ctx, string prompt, string baseUrl)
        {
            if (!string.IsNullOrEmpty(ctx.ReplyToEventId))
            {
                await ctx.Client.SendMessageAsync(ctx.RoomId, "`Note: img2img is not wired for Comfy yet; running text-to-image.`");
            }

            var workflow = JsonNode.Parse("""
            {
              "5": {"inputs": {"width": 1024, "height": 1024, "batch_size": 1}, "class_type": "EmptyLatentImage"},
              "6": {"inputs": {"text": "PROMPT_HERE", "clip": ["11", 0]}, "class_type": "CLIPTextEncode"},
              "8": {"inputs": {"samples": ["13", 0], "vae": ["10", 0]}, "class_type": "VAEDecode"},
              "9": {"inputs": {"filename_prefix": "ComputerBot", "images": ["8", 0]}, "class_type": "SaveImage"},
              "10": {"inputs": {"vae_name": "flux-vae-bf16.safetensors"}, "class_type": "VAELoader"},
              "11": {"inputs": {"clip_name1": "t5xxl_fp16.safetensors", "clip_name2": "clip_l.safetensors", "type": "flux"}, "class_type": "DualCLIPLoader"},
              "12": {"inputs": {"unet_name": "flux1-schnell-fp8.safetensors", "weight_dtype": "default"}, "class_type": "UNETLoader"},
              "13": {"inputs": {"noise": ["25", 0], "guider": ["22", 0], "sampler": ["16", 0], "sigmas": ["17", 0], "latent_image": ["5", 0]}, "class_type": "SamplerCustomAdvanced"},
              "16": {"inputs": {"sampler_name": "euler"}, "class_type": "KSamplerSelect"},
              "17": {"inputs": {"scheduler": "simple", "steps": 4, "denoise": 1.0, "model": ["12", 0]}, "class_type": "BasicScheduler"},
              "22": {"inputs": {"model": ["12", 0], "conditioning": ["6", 0]}, "class_type": "BasicGuider"},
              "25": {"inputs": {"noise_seed": 1}, "class_type": "RandomNoise"}
            }
            """)!;

            workflow["6"]!["inputs"]!["text"] = prompt;
            workflow["25"]!["inputs"]!["noise_seed"] = Random.Shared.NextInt64(1, long.MaxValue);

            var comfyReq = new JsonObject
            {
                ["prompt"] = workflow
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var submitResp = await _http.PostAsync($"{baseUrl}/prompt", new StringContent(comfyReq.ToJsonString(), Encoding.UTF8, "application/json"), cts.Token);
            submitResp.EnsureSuccessStatusCode();

            var submitJson = JsonNode.Parse(await submitResp.Content.ReadAsStringAsync())!;
            var promptId = submitJson["prompt_id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(promptId))
            {
                throw new Exception("ComfyUI did not return prompt_id");
            }

            JsonNode? historyNode = null;
            var started = DateTime.UtcNow;
            while ((DateTime.UtcNow - started) < TimeSpan.FromMinutes(10))
            {
                await Task.Delay(2000, cts.Token);
                var histResp = await _http.GetAsync($"{baseUrl}/history/{promptId}", cts.Token);
                histResp.EnsureSuccessStatusCode();
                var histJson = JsonNode.Parse(await histResp.Content.ReadAsStringAsync());

                historyNode = histJson?[promptId];
                if (historyNode != null)
                {
                    var images = historyNode["outputs"]?["9"]?["images"]?.AsArray();
                    if (images != null && images.Count > 0)
                    {
                        var imageMeta = images[0]!;
                        var filename = Uri.EscapeDataString(imageMeta["filename"]!.GetValue<string>());
                        var subfolder = Uri.EscapeDataString(imageMeta["subfolder"]?.GetValue<string>() ?? "");
                        var type = Uri.EscapeDataString(imageMeta["type"]?.GetValue<string>() ?? "output");

                        var imgBytes = await _http.GetByteArrayAsync($"{baseUrl}/view?filename={filename}&subfolder={subfolder}&type={type}", cts.Token);
                        var outName = $"sd_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.png";
                        await ctx.ImageRouter.SendImageWithRoutingAsync(ctx.Client, ctx.Db, ctx.RoomId, outName, imgBytes);
                        return;
                    }
                }
            }

            throw new TimeoutException("ComfyUI generation timed out after 10 minutes.");
        }
    }

    public class DrawCommand : StableDiffusionCommand
    {
        public override string Trigger => "!draw";
    }

    public class FluxCommand : StableDiffusionCommand
    {
        public override string Trigger => "!flux";

        protected override string GetSdBaseUrl()
        {
            var baseUrl = Environment.GetEnvironmentVariable("FLUX_API_BASE");
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                baseUrl = "http://comfyui:8188";
            }
            return baseUrl.TrimEnd('/');
        }

        protected override bool UseComfyBackend(string baseUrl)
        {
            return true;
        }
    }
}
