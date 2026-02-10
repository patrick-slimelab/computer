using System;
using System.Linq;
using System.Threading.Tasks;
using ComputerBot.Abstractions;
using ComputerBot.Data;

namespace ComputerBot.Commands
{
    public class ImageChannelCommand : ICommand
    {
        public string Trigger => "!image-channel";

        public async Task ExecuteAsync(CommandContext ctx)
        {
            var rootUser = Environment.GetEnvironmentVariable("ROOT_USER_ID");
            if (ctx.SenderId != rootUser)
            {
                await ctx.Client.SendMessageAsync(ctx.RoomId, "`Error: Unauthorized. Only root user can configure channels.`");
                return;
            }

            var parts = ctx.Args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1)
            {
                await ctx.Client.SendMessageAsync(ctx.RoomId, "`Usage: !image-channel <source> <target> OR !image-channel remove <source>`");
                return;
            }

            string first = parts[0];
            
            if (first == "remove" && parts.Length >= 2)
            {
                string sourceAlias = parts[1];
                string sourceId = await ctx.MatrixService.ResolveRoomId(sourceAlias);
                
                var mapping = ctx.Db.ImageChannelMappings.FirstOrDefault(m => m.SourceRoomId == sourceId);
                if (mapping != null)
                {
                    ctx.Db.ImageChannelMappings.Remove(mapping);
                    await ctx.Db.SaveChangesAsync();
                    await ctx.Client.SendMessageAsync(ctx.RoomId, $"`Removed image routing for {sourceAlias} ({sourceId})`");
                }
                else
                {
                    await ctx.Client.SendMessageAsync(ctx.RoomId, $"`No mapping found for {sourceAlias}`");
                }
                return;
            }

            if (parts.Length >= 2)
            {
                string sourceAlias = parts[0];
                string targetAlias = parts[1];

                try 
                {
                    string sourceId = await ctx.MatrixService.ResolveRoomId(sourceAlias);
                    string targetId = await ctx.MatrixService.ResolveRoomId(targetAlias);

                    var mapping = ctx.Db.ImageChannelMappings.FirstOrDefault(m => m.SourceRoomId == sourceId);
                    if (mapping == null)
                    {
                        mapping = new ImageChannelMapping { SourceRoomId = sourceId, TargetRoomId = targetId };
                        ctx.Db.ImageChannelMappings.Add(mapping);
                    }
                    else
                    {
                        mapping.TargetRoomId = targetId;
                    }
                    
                    await ctx.Db.SaveChangesAsync();
                    await ctx.Client.SendMessageAsync(ctx.RoomId, $"`Images from {sourceAlias} will now go to {targetAlias}`");
                }
                catch (Exception ex)
                {
                    await ctx.Client.SendMessageAsync(ctx.RoomId, $"`Error resolving rooms: {ex.Message}`");
                }
            }
            else
            {
                await ctx.Client.SendMessageAsync(ctx.RoomId, "`Usage: !image-channel <source> <target>`");
            }
        }
    }
}
