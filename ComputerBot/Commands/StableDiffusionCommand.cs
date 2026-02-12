using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
                string endpoint = "http://robokrabs:7860/sdapi/v1/txt2img";
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
                            var bytes = await ctx.Client.GetMxcImage(imgEvent.MxcUrl);
                            var base64 = Convert.ToBase64String(bytes);
                            
                            endpoint = "http://robokrabs:7860/sdapi/v1/img2img";
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
                        await ctx.Client.SendMessageAsync(ctx.RoomId, $"`Warning: Could not download reply image. Falling back to text-to-image...`");
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

                // 10 minute timeout
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
            catch (Exception ex)
            {
                Console.WriteLine($"SD Error: {ex}");
                await ctx.Client.SendMessageAsync(ctx.RoomId, $"`Error generating image: {ex.Message}`");
            }
        }
    }

    public class DrawCommand : StableDiffusionCommand
    {
        public override string Trigger => "!draw";
    }
}
