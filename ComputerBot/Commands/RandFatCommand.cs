using System;
using System.Threading.Tasks;
using ComputerBot.Abstractions;

namespace ComputerBot.Commands
{
    public class RandFatCommand : ICommand
    {
        public virtual string Trigger => "!randfat";

        public async Task ExecuteAsync(CommandContext ctx)
        {
            var valid = await RandTextFilters.GetValidMessagesAsync(
                ctx,
                RandTextFilters.RandFatBodyRegex,
                RandTextFilters.IsRandFat,
                RandTextFilters.NormalizeRandFatBody,
                sampleSize: 500);

            if (valid.Count > 0)
            {
                var rand = new Random();
                var choice = valid[rand.Next(valid.Count)];
                await ctx.Client.SendMessageAsync(ctx.RoomId, $"`{choice}`");
            }
            else
            {
                await ctx.Client.SendMessageAsync(ctx.RoomId, "`NO FULLWIDTH FOUND`");
            }
        }
    }

    public class RandWideAliasCommand : RandFatCommand
    {
        public override string Trigger => "!randwide";
    }

    public class RandFatShortAliasCommand : RandFatCommand
    {
        public override string Trigger => "!rf";
    }

    public class RandWideShortAliasCommand : RandFatCommand
    {
        public override string Trigger => "!rw";
    }
}
