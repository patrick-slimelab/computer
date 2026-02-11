using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ComputerBot.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Drawing;
using System.Linq;

namespace ComputerBot.Commands
{
    public class ZowCommand : ICommand
    {
        public string Trigger => "!zow";

        public async Task ExecuteAsync(CommandContext ctx)
        {
            var parts = ctx.Args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1 || !double.TryParse(parts[0], out double seed))
            {
                await ctx.Client.SendMessageAsync(ctx.RoomId, "`Usage: !zow <seed>`");
                return;
            }

            int width = 512;
            int height = 512;
            using var image = new Image<Rgba32>(width, height);
            image.Mutate(x => x.Fill(Color.Black));

            var points = new List<PointF>();
            float centerX = width / 2f;
            float centerY = height / 2f;

            for (int i = 0; i < 500; i++)
            {
                double angle = i * seed * (1.0 / Math.PI);
                float r = i * 0.45f;
                float x = centerX + r * (float)Math.Cos(angle);
                float y = centerY + r * (float)Math.Sin(angle);
                points.Add(new PointF(x, y));
            }

            if (points.Count > 1)
            {
                var pb = new PathBuilder();
                pb.AddLines(points);
                var path = pb.Build();
                image.Mutate(x => x.Draw(Color.Lime, 1.5f, path));
            }

            using var ms = new MemoryStream();
            await image.SaveAsPngAsync(ms);
            var bytes = ms.ToArray();

            await ctx.ImageRouter.SendImageWithRoutingAsync(ctx.Client, ctx.Db, ctx.RoomId, $"zow_{seed}.png", bytes);
            Console.WriteLine($"Sent ZOW {seed}");
        }
    }
}
