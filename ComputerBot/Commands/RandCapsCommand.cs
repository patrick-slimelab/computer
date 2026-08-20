using System;
using System.Threading.Tasks;
using ComputerBot.Abstractions;

namespace ComputerBot.Commands
{
    public class RandCapsCommand : ICommand
    {
        public virtual string Trigger => "!randcaps";

        public async Task ExecuteAsync(CommandContext ctx)
        {
            var valid = await RandTextFilters.GetValidMessagesAsync(
                ctx,
                RandTextFilters.RandCapsBodyRegex,
                RandTextFilters.IsRandCaps);

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
