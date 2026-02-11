using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ComputerBot.Abstractions;

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
            // Optional: send typing or "Generating..."
            
            try
            {
                var payload = new
                {
                    prompt = prompt,
                    steps = 20,
                    width = 512,
                    height = 512,
                    sampler_name = "Euler a"
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                
                // 10 minute timeout for slow generations
                _http.Timeout = TimeSpan.FromMinutes(10);
                
                var response = await _http.PostAsync("http://robokrabs:7860/sdapi/v1/txt2img", content);
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
