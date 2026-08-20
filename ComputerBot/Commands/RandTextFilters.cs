using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ComputerBot.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ComputerBot.Commands
{
    internal static class RandTextFilters
    {
        public const string RandCapsBodyRegex = "^[^a-z]+$";
        public const string RandFatBodyRegex = "[A-Za-z](?: +[A-Za-z]){3,}";

        private static readonly string[] BaseBlacklist = {
            "@fish:cclub.cs.wmich.edu",
            "@rustix:cclub.cs.wmich.edu",
            "@gooey:cclub.cs.wmich.edu"
        };

        public static async Task<List<string>> GetValidMessagesAsync(
            CommandContext ctx,
            string bodyRegex,
            Func<string, bool> isValid,
            Func<string, string>? normalize = null,
            int sampleSize = 50)
        {
            var filterBuilder = Builders<BsonDocument>.Filter;
            var query = ParseQuery(ctx.Args);

            var blacklist = BaseBlacklist.ToList();
            if (!string.IsNullOrEmpty(ctx.Client.UserId)) blacklist.Add(ctx.Client.UserId);

            var filter = filterBuilder.Regex("content.body", new BsonRegularExpression(bodyRegex)) &
                         filterBuilder.Eq("type", "m.room.message") &
                         filterBuilder.Nin("sender", blacklist);

            if (!string.IsNullOrEmpty(query.SenderRegex))
            {
                filter &= filterBuilder.Regex("sender", new BsonRegularExpression(query.SenderRegex, "i"));
            }

            if (!string.IsNullOrEmpty(query.GrepRegex))
            {
                ValidateRegex(query.GrepRegex, "--grep");
                filter &= filterBuilder.Regex("content.body", new BsonRegularExpression(query.GrepRegex, "i"));
            }

            var pipeline = new EmptyPipelineDefinition<BsonDocument>()
                .Match(filter)
                .Sample(sampleSize);

            var candidates = await ctx.Events.Aggregate(pipeline).ToListAsync();

            return candidates
                .Select(doc => doc["content"]["body"].AsString)
                .Select(body => normalize?.Invoke(body) ?? body)
                .Where(isValid)
                .ToList();
        }

        private static RandTextQuery ParseQuery(string? args)
        {
            args = args?.Trim();
            if (string.IsNullOrEmpty(args))
            {
                return new RandTextQuery(null, null);
            }

            var grepMatch = Regex.Match(args, @"(?:^|\s)--grep(?:=|\s+)(?<grep>.+)$", RegexOptions.Singleline);
            if (!grepMatch.Success)
            {
                return new RandTextQuery(args, null);
            }

            var senderRegex = args[..grepMatch.Index].Trim();
            var grepRegex = Unquote(grepMatch.Groups["grep"].Value.Trim());

            return new RandTextQuery(
                string.IsNullOrEmpty(senderRegex) ? null : senderRegex,
                string.IsNullOrEmpty(grepRegex) ? null : grepRegex);
        }

        private static string Unquote(string value)
        {
            if (value.Length < 2)
            {
                return value;
            }

            var quote = value[0];
            if ((quote != '"' && quote != '\'') || value[^1] != quote)
            {
                return value;
            }

            return value[1..^1];
        }

        private static void ValidateRegex(string pattern, string name)
        {
            try
            {
                _ = new Regex(pattern, RegexOptions.IgnoreCase);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"Invalid {name} regex: {ex.Message}", ex);
            }
        }

        public static bool IsRandCaps(string body)
        {
            return HasEnoughLetters(body) && body.Where(char.IsLetter).All(char.IsUpper);
        }

        public static bool IsRandFat(string body)
        {
            return HasEnoughLetters(body, ignoreWhitespace: true, minimumLength: 4) &&
                   body.All(c => !char.IsDigit(c)) &&
                   body.All(IsFullwidthDisplayCharacter) &&
                   body.Where(char.IsLetter).All(IsFullwidthLatinLetter);
        }

        public static string NormalizeRandFatBody(string body)
        {
            if (IsSpacedAsciiLatinText(body))
            {
                return ToFullwidthSpacedAscii(body);
            }

            return string.Empty;
        }

        private static string ToFullwidthSpacedAscii(string body)
        {
            var sb = new StringBuilder(body.Length);

            for (var i = 0; i < body.Length; i++)
            {
                var c = body[i];

                if (IsAsciiLatinLetter(c))
                {
                    sb.Append((char)(c + 0xFEE0));
                }
                else if (c == ' ')
                {
                    var runLength = 1;
                    while (i + runLength < body.Length && body[i + runLength] == ' ')
                    {
                        runLength++;
                    }

                    if (runLength > 1)
                    {
                        sb.Append('　');
                    }

                    i += runLength - 1;
                }
            }

            return sb.ToString();
        }

        private static bool IsSpacedAsciiLatinText(string body)
        {
            if (string.IsNullOrWhiteSpace(body) || body.Trim() != body || !body.Any(IsAsciiLatinLetter))
            {
                return false;
            }

            var wordStart = 0;
            var i = 0;
            while (i < body.Length)
            {
                if (body[i] != ' ')
                {
                    if (!IsAsciiLatinLetter(body[i]))
                    {
                        return false;
                    }

                    i++;
                    continue;
                }

                var runStart = i;
                while (i < body.Length && body[i] == ' ')
                {
                    i++;
                }

                var runLength = i - runStart;
                if (runLength >= 2)
                {
                    if (runStart == wordStart || !IsSpacedAsciiWord(body, wordStart, runStart))
                    {
                        return false;
                    }

                    wordStart = i;
                }
            }

            return wordStart < body.Length && IsSpacedAsciiWord(body, wordStart, body.Length);
        }

        private static bool IsSpacedAsciiWord(string body, int start, int end)
        {
            var expectLetter = true;
            for (var i = start; i < end; i++)
            {
                if (expectLetter)
                {
                    if (!IsAsciiLatinLetter(body[i]))
                    {
                        return false;
                    }
                }
                else if (body[i] != ' ')
                {
                    return false;
                }

                expectLetter = !expectLetter;
            }

            return !expectLetter;
        }

        private static bool HasEnoughLetters(string body, bool ignoreWhitespace = false, int minimumLength = 11)
        {
            var length = ignoreWhitespace
                ? body.Count(c => !char.IsWhiteSpace(c))
                : body.Length;

            return length >= minimumLength && body.Count(char.IsLetter) / (double)length >= 0.6;
        }

        private static bool IsFullwidthLatinLetter(char c)
        {
            return c is >= 'Ａ' and <= 'Ｚ' or >= 'ａ' and <= 'ｚ';
        }

        private static bool IsAsciiLatinLetter(char c)
        {
            return c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
        }

        private static bool IsFullwidthDisplayCharacter(char c)
        {
            return c is >= '！' and <= '～' or '　';
        }

        private sealed record RandTextQuery(string? SenderRegex, string? GrepRegex);
    }
}
