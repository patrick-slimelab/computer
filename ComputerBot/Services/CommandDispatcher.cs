using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ComputerBot.Abstractions;
using ComputerBot.Data;
using Matrix.Sdk.Core.Domain.RoomEvent;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ComputerBot.Services
{
    public class CommandDispatcher
    {
        private readonly Dictionary<string, ICommand> _commands = new();
        private readonly MatrixService _matrix;
        private readonly IMongoCollection<BsonDocument> _events;

        public CommandDispatcher(MatrixService matrix, IMongoCollection<BsonDocument> events)
        {
            _matrix = matrix;
            _events = events;
            RegisterCommands();
        }

        private void RegisterCommands()
        {
            var types = Assembly.GetExecutingAssembly().GetTypes()
                .Where(t => typeof(ICommand).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

            foreach (var t in types)
            {
                if (Activator.CreateInstance(t) is ICommand cmd)
                {
                    _commands[cmd.Trigger] = cmd;
                    Console.WriteLine($"Registered command: {cmd.Trigger}");
                }
            }
        }

        public async Task HandleEvent(TextMessageEvent textEvent)
        {
            var msg = textEvent.Message?.Trim();
            if (string.IsNullOrEmpty(msg)) return;

            var parts = msg.Split(' ', 2);
            var trigger = parts[0];
            var args = parts.Length > 1 ? parts[1] : "";

            if (_commands.TryGetValue(trigger, out var cmd))
            {
                Console.WriteLine($"Executing {trigger} for {textEvent.SenderUserId} in {textEvent.RoomId}");
                
                using var db = new BotDbContext();
                if (db.HandledEvents.Any(e => e.EventId == textEvent.EventId)) return;

                try
                {
                    var ctx = new CommandContext(
                        _matrix.Client,
                        db,
                        _events,
                        textEvent.RoomId,
                        textEvent.SenderUserId,
                        args,
                        _matrix
                    );
                    
                    await cmd.ExecuteAsync(ctx);

                    db.HandledEvents.Add(new HandledEvent 
                    { 
                        EventId = textEvent.EventId, 
                        ProcessedAt = DateTime.UtcNow 
                    });
                    await db.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error executing {trigger}: {ex}");
                    await _matrix.Client.SendMessageAsync(textEvent.RoomId, $"`Error: {ex.Message}`");
                }
            }
        }
    }
}
