using System;
using System.Threading.Tasks;
using ComputerBot.Abstractions;

namespace ComputerBot.Commands
{
    public class ComputerNickCommand : ICommand
    {
        public string Trigger => "!computernick";

        public async Task ExecuteAsync(CommandContext ctx)
        {
            var rootUser = Environment.GetEnvironmentVariable("ROOT_USER_ID");
            if (ctx.SenderId != rootUser)
            {
                await ctx.Client.SendMessageAsync(ctx.RoomId, "`Error: Unauthorized. Only root user can change my nick.`");
                return;
            }

            var displayName = ctx.Args.Trim();
            if (string.IsNullOrWhiteSpace(displayName))
            {
                await ctx.Client.SendMessageAsync(ctx.RoomId, "`Usage: !computernick <display name>`");
                return;
            }

            if (displayName.Equals("--clear", StringComparison.OrdinalIgnoreCase) ||
                displayName.Equals("clear", StringComparison.OrdinalIgnoreCase) ||
                displayName.Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                displayName = string.Empty;
            }

            await ctx.MatrixService.SetOwnDisplayNameAsync(ctx.RoomId, displayName);

            var message = string.IsNullOrEmpty(displayName)
                ? "`Cleared my Matrix display name in this room.`"
                : $"`Updated my Matrix display name in this room to {displayName}.`";
            await ctx.Client.SendMessageAsync(ctx.RoomId, message);
        }
    }
}
