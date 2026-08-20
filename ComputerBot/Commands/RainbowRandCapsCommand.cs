using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using ComputerBot.Abstractions;

namespace ComputerBot.Commands
{
    public class RainbowRandCapsCommand : ICommand
    {
        public virtual string Trigger => "!rainbowrandcaps";

        private static readonly string[] RainbowColors = {
            "#ff3b30",
            "#ff9500",
            "#ffcc00",
            "#34c759",
            "#32ade6",
            "#007aff",
            "#af52de"
        };

        public async Task ExecuteAsync(CommandContext ctx)
        {
            var valid = await RandTextFilters.GetValidMessagesAsync(
                ctx,
                RandTextFilters.RandCapsBodyRegex,
                RandTextFilters.IsRandCaps);

            if (valid.Count == 0)
            {
                await ctx.Client.SendMessageAsync(ctx.RoomId, "`NO SCREAMING FOUND`");
                return;
            }

            var rand = new Random();
            var choice = valid[rand.Next(valid.Count)];
            await ctx.Client.SendFormattedMessageAsync(ctx.RoomId, choice, Rainbowize(choice));
        }

        internal static string Rainbowize(string text)
        {
            var sb = new StringBuilder();
            var colorIndex = 0;

            foreach (var c in text)
            {
                var encoded = WebUtility.HtmlEncode(c.ToString());
                if (char.IsWhiteSpace(c))
                {
                    sb.Append(encoded);
                    continue;
                }

                var color = RainbowColors[colorIndex % RainbowColors.Length];
                // Use Matrix's legacy color form for mobile compatibility.
                // Nesting this with the newer span form causes some mobile
                // sanitizers to discard the rendered contents entirely.
                sb.Append("<font color=\"")
                    .Append(color)
                    .Append("\" data-mx-color=\"")
                    .Append(color)
                    .Append("\">")
                    .Append(encoded)
                    .Append("</font>");
                colorIndex++;
            }

            return sb.ToString();
        }
    }

    public class RainbowRandCapsShortAliasCommand : RainbowRandCapsCommand
    {
        public override string Trigger => "!rrc";
    }

    public class RainbowRandCapsAliasCommand : RainbowRandCapsCommand
    {
        public override string Trigger => "!rrandcaps";
    }

    public class RainbowRandFatCommand : ICommand
    {
        public virtual string Trigger => "!rainbowrandfat";

        public async Task ExecuteAsync(CommandContext ctx)
        {
            var valid = await RandTextFilters.GetValidMessagesAsync(
                ctx,
                RandTextFilters.RandFatBodyRegex,
                RandTextFilters.IsRandFat,
                RandTextFilters.NormalizeRandFatBody,
                sampleSize: 500);

            if (valid.Count == 0)
            {
                await ctx.Client.SendMessageAsync(ctx.RoomId, "`NO FULLWIDTH FOUND`");
                return;
            }

            var rand = new Random();
            var choice = valid[rand.Next(valid.Count)];
            await ctx.Client.SendFormattedMessageAsync(ctx.RoomId, choice, RainbowRandCapsCommand.Rainbowize(choice));
        }
    }

    public class RainbowRandWideAliasCommand : RainbowRandFatCommand
    {
        public override string Trigger => "!rainbowrandwide";
    }

    public class RainbowRandFShortenedAliasCommand : RainbowRandFatCommand
    {
        public override string Trigger => "!rainbowrandf";
    }

    public class RainbowRandWShortenedAliasCommand : RainbowRandFatCommand
    {
        public override string Trigger => "!rainbowrandw";
    }

    public class RainbowRFatAliasCommand : RainbowRandFatCommand
    {
        public override string Trigger => "!rainbowrfat";
    }

    public class RainbowRWideAliasCommand : RainbowRandFatCommand
    {
        public override string Trigger => "!rainbowrwide";
    }

    public class RainbowRandFatLongAliasCommand : RainbowRandFatCommand
    {
        public override string Trigger => "!rrandfat";
    }

    public class RainbowRandWideLongAliasCommand : RainbowRandFatCommand
    {
        public override string Trigger => "!rrandwide";
    }

    public class RainbowRandFLongAliasCommand : RainbowRandFatCommand
    {
        public override string Trigger => "!rrandf";
    }

    public class RainbowRandWLongAliasCommand : RainbowRandFatCommand
    {
        public override string Trigger => "!rrandw";
    }

    public class RainbowRandFatMediumAliasCommand : RainbowRandFatCommand
    {
        public override string Trigger => "!rrfat";
    }

    public class RainbowRandWideMediumAliasCommand : RainbowRandFatCommand
    {
        public override string Trigger => "!rrwide";
    }

    public class RainbowRandFatShortAliasCommand : RainbowRandFatCommand
    {
        public override string Trigger => "!rrf";
    }

    public class RainbowRandWideShortAliasCommand : RainbowRandFatCommand
    {
        public override string Trigger => "!rrw";
    }
}
