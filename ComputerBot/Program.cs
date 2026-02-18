using System;
using System.Threading.Tasks;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Matrix.Sdk;
using Matrix.Sdk.Core.Domain.RoomEvent;
using MongoDB.Bson;
using MongoDB.Driver;
using ComputerBot.Data;
using ComputerBot.Services;

namespace ComputerBot
{
    class Program
    {
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

            // Ensure data dir exists
            Directory.CreateDirectory("data");

            // Init SQL DB
            using (var dbContext = new BotDbContext())
            {
                dbContext.Database.EnsureCreated();
                // Migration hack: Ensure ImageChannelMappings exists
                dbContext.Database.ExecuteSqlRaw(@"
                    CREATE TABLE IF NOT EXISTS ""ImageChannelMappings"" (
                        ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_ImageChannelMappings"" PRIMARY KEY AUTOINCREMENT,
                        ""SourceRoomId"" TEXT NULL,
                        ""TargetRoomId"" TEXT NULL
                    );
                ");
            }

            // Init Mongo
            var mongoClient = new MongoClient(mongoUri);
            var db = mongoClient.GetDatabase(dbName);
            var collection = db.GetCollection<BsonDocument>("events");

            // Init Matrix
            var factory = new MatrixClientFactory();
            var client = factory.Create();
            
            var matrixService = new MatrixService(client, collection);
            var dispatcher = new CommandDispatcher(matrixService, collection);

            client.OnMatrixRoomEventsReceived += async (sender, eventArgs) =>
            {
                foreach (var roomEvent in eventArgs.MatrixRoomEvents)
                {
                    if (roomEvent is TextMessageEvent textEvent)
                    {
                        await dispatcher.HandleEvent(textEvent);
                    }
                }
            };

            await matrixService.LoginAsync(new Uri(hs), user, pass);

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1500);
                    await matrixService.AutoJoinPublicRoomsAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Auto-join worker failed: {ex.Message}");
                }
            });
            
            Console.WriteLine("Bot started (Refactored). Press Ctrl+C to exit.");
            await Task.Delay(-1);
        }
    }
}
