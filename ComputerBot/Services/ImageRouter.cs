using System.Linq;
using System.Threading.Tasks;
using ComputerBot.Data;
using Matrix.Sdk;

namespace ComputerBot.Services
{
    public class ImageRouter
    {
        public async Task SendImageWithRoutingAsync(IMatrixClient client, BotDbContext db, string roomId, string filename, byte[] data)
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
                var link = $"https://matrix.to/#/{targetRoom}/{eventId}";
                await client.SendMessageAsync(roomId, $"`Image posted to image channel`: {link}");
            }
        }
    }
}
