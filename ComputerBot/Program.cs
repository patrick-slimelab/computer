using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Matrix.Sdk;
using Matrix.Sdk.Core.Domain.RoomEvent;
using MongoDB.Bson;
using MongoDB.Driver;
using ComputerBot.Data;

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

            // Build full blacklist including self
            var blacklist = BaseBlacklist.ToList();
            if (!string.IsNullOrEmpty(user) && !blacklist.Contains(user))
            {
                blacklist.Add(user);
            }

            // Init DB
            using (var dbContext = new BotDbContext())
            {
                dbContext.Database.EnsureCreated();
            }

            // Setup Mongo
            var mongoClient = new MongoClient(mongoUri);
            var db = mongoClient.GetDatabase(dbName);
            var collection = db.GetCollection<BsonDocument>("events");

            // Setup Matrix
            var factory = new MatrixClientFactory();
            var client = factory.Create();

            client.OnMatrixRoomEventsReceived += async (sender, eventArgs) =>
            {
                using var dbContext = new BotDbContext();
                foreach (var roomEvent in eventArgs.MatrixRoomEvents)
                {
                    if (roomEvent is not TextMessageEvent textEvent) continue;
                    
                    // Deduplicate
                    if (dbContext.HandledEvents.Any(e => e.EventId == textEvent.EventId)) continue;

                    var roomId = textEvent.RoomId;
                    var senderId = textEvent.SenderUserId;
                    var message = textEvent.Message;

                    if (string.IsNullOrWhiteSpace(message)) continue;

                    if (message.Trim().StartsWith("!randcaps"))
                    {
                        Console.WriteLine($"Command from {senderId} in {roomId}: {message}");
                        await HandleRandCaps(client, collection, roomId, blacklist);

                        // Mark handled
                        dbContext.HandledEvents.Add(new HandledEvent 
                        { 
                            EventId = textEvent.EventId, 
                            ProcessedAt = DateTime.UtcNow 
                        });
                        await dbContext.SaveChangesAsync();
                    }
                }
            };

            Console.WriteLine($"Logging in as {user} on {hs}...");
            await client.LoginAsync(new Uri(hs), user, pass, "computer-bot");
            
            client.Start();
            Console.WriteLine("Bot started. Press Ctrl+C to exit.");

            await Task.Delay(-1);
        }

        static async Task HandleRandCaps(IMatrixClient client, IMongoCollection<BsonDocument> collection, string roomId, IEnumerable<string> blacklist)
        {
            try 
            {
                var filterBuilder = Builders<BsonDocument>.Filter;
                // Regex: Start with non-lowercase, end with non-lowercase
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
