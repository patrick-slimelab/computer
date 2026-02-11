using System.Threading.Tasks;
using ComputerBot.Data;
using ComputerBot.Services;
using Matrix.Sdk;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ComputerBot.Abstractions
{
    public record CommandContext(
        IMatrixClient Client,
        BotDbContext Db,
        IMongoCollection<BsonDocument> Events,
        string RoomId,
        string SenderId,
        string Args,
        MatrixService MatrixService,
        ImageRouter ImageRouter
    );

    public interface ICommand
    {
        string Trigger { get; }
        Task ExecuteAsync(CommandContext ctx);
    }
}
