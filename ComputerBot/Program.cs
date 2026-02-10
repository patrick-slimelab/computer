using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using Matrix.Sdk;
using Matrix.Sdk.Core.Domain.RoomEvent;
using MongoDB.Bson;
using MongoDB.Driver;
using ComputerBot.Data;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Drawing;

namespace ComputerBot
{
    class Program
    {
        private static readonly string[] BaseBlacklist = {
            "@fish:cclub.cs.wmich.edu",
            "@rustix:cclub.cs.wmich.edu",
            "@gooey:cclub.cs.wmich.edu"
        };

        static async Task Main(string[] args)
        {
            var hs = Environment.GetEnvironmentVariable("MATRIX_HOMESERVER") ?? "https://matrix.org";
            var user = Environment.GetEnvironmentVariable("MATRIX_USER_ID");
            var pass = Environment.GetEnvironmentVariable("MATRIX_PASSWORD");
            var mongoUri = Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://mongo:27017";
            var dbName = Environment.GetEnvironmentVariable("MONGODB_DB") ?? "matrix_index";

            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
            {
                Console.WriteLine("Missing MATRIX_USER_ID or MATRIX_PASSWORD");
                return;
            }

            var blacklist = BaseBlacklist.ToList();
            if (!string.IsNullOrEmpty(user) && !blacklist.Contains(user))
            {
                blacklist.Add(user);
            }

            using (var dbContext = new BotDbContext())
            {
                dbContext.Database.EnsureCreated();
            }

            var mongoClient = new MongoClient(mongoUri);
            var db = mongoClient.GetDatabase(dbName);
            var collection = db.GetCollection<BsonDocument>("events");

            var factory = new MatrixClientFactory();
            var client = factory.Create();

            client.OnMatrixRoomEventsReceived += async (sender, eventArgs) =>
            {
                using var dbContext = new BotDbContext();
                foreach (var roomEvent in eventArgs.MatrixRoomEvents)
                {
                    if (roomEvent is not TextMessageEvent textEvent) continue;
                    
                    if (dbContext.HandledEvents.Any(e => e.EventId == textEvent.EventId)) continue;

                    var roomId = textEvent.RoomId;
                    var senderId = textEvent.SenderUserId;
                    var message = textEvent.Message;

                    if (string.IsNullOrWhiteSpace(message)) continue;

                    var trimmed = message.Trim();
                    if (trimmed.StartsWith("!randcaps"))
                    {
                        Console.WriteLine($"Randcaps from {senderId} in {roomId}");
                        await HandleRandCaps(client, collection, roomId, blacklist);
                        await MarkHandled(dbContext, textEvent.EventId);
                    }
                    else if (trimmed.StartsWith("!zow"))
                    {
                        Console.WriteLine($"Zow from {senderId} in {roomId}");
                        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 1 && double.TryParse(parts[1], out double seed))
                        {
                            await HandleZow(client, roomId, seed);
                        }
                        else
                        {
                            await client.SendMessageAsync(roomId, "`Usage: !zow <seed>`");
                        }
                        await MarkHandled(dbContext, textEvent.EventId);
                    }
                }
            };

            Console.WriteLine($"Logging in as {user} on {hs}...");
            await client.LoginAsync(new Uri(hs), user, pass, "computer-bot");
            
            client.Start();
            Console.WriteLine("Bot started. Press Ctrl+C to exit.");

            await Task.Delay(-1);
        }

        static async Task MarkHandled(BotDbContext db, string eventId)
        {
            db.HandledEvents.Add(new HandledEvent { EventId = eventId, ProcessedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        static async Task HandleZow(IMatrixClient client, string roomId, double seed)
        {
            try
            {
                int width = 512;
                int height = 512;
                using var image = new Image<Rgba32>(width, height);
                
                // Black background
                image.Mutate(x => x.Fill(Color.Black));

                var points = new List<PointF>();
                float centerX = width / 2f;
                float centerY = height / 2f;

                // Algorithm: 
                // for (let i = 0; i < 500; i++) {
                //   const angle = i * seed * (1 / Math.PI);
                //   const x = centerX + i * Math.cos(angle);
                //   const y = centerY + i * Math.sin(angle);
                // }
                
                // Scale i? 500 radius is too big for 512x512 (radius 256). 
                // Let's scale i by 0.45 to fit comfortably.
                
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
                    // Draw lines using PathBuilder
                    var pb = new PathBuilder();
                    pb.AddLines(points);
                    var path = pb.Build();
                    
                    image.Mutate(x => x.Draw(Color.Lime, 1.5f, path));
                }

                using var ms = new MemoryStream();
                await image.SaveAsPngAsync(ms);
                var bytes = ms.ToArray();

                await client.SendImageAsync(roomId, $"zow_{seed}.png", bytes);
                Console.WriteLine($"Sent ZOW {seed}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Zow Error: {ex}");
                await client.SendMessageAsync(roomId, $"`Error generating ZOW: {ex.Message}`");
            }
        }

        static async Task HandleRandCaps(IMatrixClient client, IMongoCollection<BsonDocument> collection, string roomId, IEnumerable<string> blacklist)
        {
            try 
            {
                var filterBuilder = Builders<BsonDocument>.Filter;
                var filter = filterBuilder.Regex("content.body", new BsonRegularExpression("^[^a-z]+$")) &
                             filterBuilder.Eq("type", "m.room.message") &
                             filterBuilder.Nin("sender", blacklist);

                var pipeline = new EmptyPipelineDefinition<BsonDocument>()
                    .Match(filter)
                    .Sample(50);

                var candidates = await collection.Aggregate(pipeline).ToListAsync();
                
                var valid = candidates
                    .Select(doc => doc["content"]["body"].AsString)
                    .Where(body => body.Length > 10)
                    .Where(body => body.Count(char.IsLetter) / (double)body.Length >= 0.6)
                    .ToList();

                if (valid.Count > 0)
                {
                    var rand = new Random();
                    var choice = valid[rand.Next(valid.Count)];
                    Console.WriteLine($"Selected: {choice}");
                    await client.SendMessageAsync(roomId, $"`{choice}`");
                }
                else
                {
                    Console.WriteLine("No candidates found after filtering");
                    await client.SendMessageAsync(roomId, "`NO SCREAMING FOUND`");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex}");
                await client.SendMessageAsync(roomId, $"`Error: {ex.Message}`");
            }
        }
    }
}
