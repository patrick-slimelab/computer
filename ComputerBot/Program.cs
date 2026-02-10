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
            var rootUser = Environment.GetEnvironmentVariable("ROOT_USER_ID");

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

            // Ensure data dir exists
            Directory.CreateDirectory("data");

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
                    try 
                    {
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
                                await HandleZow(client, dbContext, roomId, seed);
                            }
                            else
                            {
                                await client.SendMessageAsync(roomId, "`Usage: !zow <seed>`");
                            }
                            await MarkHandled(dbContext, textEvent.EventId);
                        }
                        else if (trimmed.StartsWith("!image-channel"))
                        {
                            Console.WriteLine($"ImageChannel from {senderId} in {roomId}");
                            if (senderId != rootUser)
                            {
                                await client.SendMessageAsync(roomId, "`Error: Unauthorized. Only root user can configure channels.`");
                            }
                            else
                            {
                                await HandleImageChannel(client, dbContext, roomId, trimmed);
                            }
                            await MarkHandled(dbContext, textEvent.EventId);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Top-level handler error: {ex}");
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

        static async Task HandleImageChannel(IMatrixClient client, BotDbContext db, string roomId, string command)
        {
            // !image-channel #source #target
            // !image-channel remove #source
            
            var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                await client.SendMessageAsync(roomId, "`Usage: !image-channel <source> <target> OR !image-channel remove <source>`");
                return;
            }

            string action = parts[1];
            
            if (action == "remove" && parts.Length >= 3)
            {
                string sourceAlias = parts[2];
                string sourceId = await ResolveRoomId(client, sourceAlias);
                
                var mapping = db.ImageChannelMappings.FirstOrDefault(m => m.SourceRoomId == sourceId);
                if (mapping != null)
                {
                    db.ImageChannelMappings.Remove(mapping);
                    await db.SaveChangesAsync();
                    await client.SendMessageAsync(roomId, $"`Removed image routing for {sourceAlias} ({sourceId})`");
                }
                else
                {
                    await client.SendMessageAsync(roomId, $"`No mapping found for {sourceAlias}`");
                }
                return;
            }

            if (parts.Length >= 3)
            {
                string sourceAlias = parts[1];
                string targetAlias = parts[2];

                try 
                {
                    string sourceId = await ResolveRoomId(client, sourceAlias);
                    string targetId = await ResolveRoomId(client, targetAlias);

                    var mapping = db.ImageChannelMappings.FirstOrDefault(m => m.SourceRoomId == sourceId);
                    if (mapping == null)
                    {
                        mapping = new ImageChannelMapping { SourceRoomId = sourceId, TargetRoomId = targetId };
                        db.ImageChannelMappings.Add(mapping);
                    }
                    else
                    {
                        mapping.TargetRoomId = targetId;
                    }
                    
                    await db.SaveChangesAsync();
                    await client.SendMessageAsync(roomId, $"`Images from {sourceAlias} will now go to {targetAlias}`");
                }
                catch (Exception ex)
                {
                    await client.SendMessageAsync(roomId, $"`Error resolving rooms: {ex.Message}`");
                }
            }
        }

        static async Task<string> ResolveRoomId(IMatrixClient client, string aliasOrId)
        {
            if (aliasOrId.StartsWith("!")) return aliasOrId; // Already an ID
            
            // Try to join/resolve
            // Note: JoinTrustedPrivateRoomAsync returns JoinRoomResponse which has RoomId
            var response = await client.JoinTrustedPrivateRoomAsync(aliasOrId);
            return response.RoomId;
        }

        static async Task HandleZow(IMatrixClient client, BotDbContext db, string roomId, double seed)
        {
            try
            {
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

                await SendImageWithRouting(client, db, roomId, $"zow_{seed}.png", bytes);
                Console.WriteLine($"Sent ZOW {seed}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Zow Error: {ex}");
                await client.SendMessageAsync(roomId, $"`Error generating ZOW: {ex.Message}`");
            }
        }

        static async Task SendImageWithRouting(IMatrixClient client, BotDbContext db, string roomId, string filename, byte[] data)
        {
            var mapping = db.ImageChannelMappings.FirstOrDefault(m => m.SourceRoomId == roomId);
            string targetRoom = roomId;
            
            if (mapping != null)
            {
                targetRoom = mapping.TargetRoomId;
            }

            var eventId = await client.SendImageAsync(targetRoom, filename, data);
            
            if (targetRoom != roomId)
            {
                // Link to the image in the target room
                // Matrix.to link: https://matrix.to/#/!roomid/eventid
                var link = $"https://matrix.to/#/{targetRoom}/{eventId}";
                await client.SendMessageAsync(roomId, $"`Image posted to image channel`: {link}");
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
