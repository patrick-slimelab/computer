using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ComputerBot.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ComputerBot.Commands
{
    public class SearchCommand : ICommand
    {
        public string Trigger => "!search";

        public async Task ExecuteAsync(CommandContext ctx)
        {
            var query = ctx.Args?.Trim();
            if (string.IsNullOrEmpty(query))
            {
                await ctx.Client.SendMessageAsync(ctx.RoomId, "`Usage: !search <query>`");
                return;
            }

            try
            {
                var filterBuilder = Builders<BsonDocument>.Filter;
                var filter = filterBuilder.Regex("content.body", new BsonRegularExpression(query, "i")) &
                             filterBuilder.Eq("type", "m.room.message");

                var sort = Builders<BsonDocument>.Sort.Descending("origin_server_ts");

                var results = await ctx.Events.Find(filter)
                    .Sort(sort)
                    .Limit(5)
                    .ToListAsync();

                if (results.Count == 0)
                {
                    await ctx.Client.SendMessageAsync(ctx.RoomId, "`No results found.`");
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine($"Search results for '{query}':");
                
                foreach (var doc in results)
                {
                    var sender = doc["sender"].AsString;
                    var body = doc.GetValue("content", new BsonDocument()).AsBsonDocument.GetValue("body", "").AsString;
                    var roomId = doc["room_id"].AsString;
                    var eventId = doc["event_id"].AsString;
                    
                    long ts = 0;
                    if (doc.Contains("origin_server_ts"))
                    {
                        var val = doc["origin_server_ts"];
                        if (val.IsInt64) ts = val.AsInt64;
                        else if (val.IsDouble) ts = (long)val.AsDouble;
                        else if (val.IsInt32) ts = val.AsInt32;
                    }
                    
                    var date = DateTimeOffset.FromUnixTimeMilliseconds(ts).ToString("yyyy-MM-dd");
                    var link = $"https://matrix.to/#/{roomId}/{eventId}";
                    
                    if (body.Length > 80) body = body.Substring(0, 77) + "...";
                    
                    // Explicit newlines for spacing
                    sb.Append($"`[{date}]` {sender}: {body} {link}\n\n");
                }

                await ctx.Client.SendMessageAsync(ctx.RoomId, sb.ToString());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Search Error: {ex}");
                await ctx.Client.SendMessageAsync(ctx.RoomId, $"`Error: {ex.Message}`");
            }
        }
    }
}
