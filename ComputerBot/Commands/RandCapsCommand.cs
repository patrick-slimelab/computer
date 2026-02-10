using System;
using System.Linq;
using System.Threading.Tasks;
using ComputerBot.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ComputerBot.Commands
{
    public class RandCapsCommand : ICommand
    {
        public string Trigger => "!randcaps";

        private static readonly string[] BaseBlacklist = {
            "@fish:cclub.cs.wmich.edu",
            "@rustix:cclub.cs.wmich.edu",
            "@gooey:cclub.cs.wmich.edu"
        };

        public async Task ExecuteAsync(CommandContext ctx)
        {
            var filterBuilder = Builders<BsonDocument>.Filter;
            
            var blacklist = BaseBlacklist.ToList();
            if (!string.IsNullOrEmpty(ctx.Client.UserId)) blacklist.Add(ctx.Client.UserId);

            var filter = filterBuilder.Regex("content.body", new BsonRegularExpression("^[^a-z]+$")) &
                         filterBuilder.Eq("type", "m.room.message") &
                         filterBuilder.Nin("sender", blacklist);

            // Optional user filter: !randcaps <username>
            var args = ctx.Args?.Trim();
            if (!string.IsNullOrEmpty(args))
            {
                // Case-insensitive sender match
                filter &= filterBuilder.Regex("sender", new BsonRegularExpression(args, "i"));
            }

            var pipeline = new EmptyPipelineDefinition<BsonDocument>()
                .Match(filter)
                .Sample(50);

            var candidates = await ctx.Events.Aggregate(pipeline).ToListAsync();
            
            var valid = candidates
                .Select(doc => doc["content"]["body"].AsString)
                .Where(body => body.Length > 10)
                .Where(body => body.Count(char.IsLetter) / (double)body.Length >= 0.6)
                .ToList();

            if (valid.Count > 0)
            {
                var rand = new Random();
                var choice = valid[rand.Next(valid.Count)];
                await ctx.Client.SendMessageAsync(ctx.RoomId, $"`{choice}`");
            }
            else
            {
                await ctx.Client.SendMessageAsync(ctx.RoomId, "`NO SCREAMING FOUND`");
            }
        }
    }
}
