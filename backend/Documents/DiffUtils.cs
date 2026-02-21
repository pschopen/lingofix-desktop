using System.Text;

namespace Lingofix.Backend.Documents;

internal enum DiffKind
{
    Equal,
    Insert,
    Delete
}

internal readonly record struct DiffOp(DiffKind Kind, string Token);

internal static class DiffUtils
{
    private const long MaxDpCells = 2_000_000;

    public static List<string> TokenizeWords(string text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return tokens;
        }

        var builder = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                continue;
            }

            FlushToken(builder, tokens);
            tokens.Add(ch.ToString());
        }

        FlushToken(builder, tokens);
        return tokens;
    }

    public static List<string> TokenizeWithWhitespaceGroups(string text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return tokens;
        }

        var builder = new StringBuilder();
        var mode = TokenMode.None;

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (mode != TokenMode.Word)
                {
                    FlushToken(builder, tokens);
                    mode = TokenMode.Word;
                }

                builder.Append(ch);
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (mode != TokenMode.Space)
                {
                    FlushToken(builder, tokens);
                    mode = TokenMode.Space;
                }

                builder.Append(ch);
                continue;
            }

            FlushToken(builder, tokens);
            mode = TokenMode.None;
            tokens.Add(ch.ToString());
        }

        FlushToken(builder, tokens);
        return tokens;
    }

    public static List<DiffOp> Diff(IReadOnlyList<string> original, IReadOnlyList<string> corrected)
    {
        var cellCount = (long)(original.Count + 1) * (corrected.Count + 1);
        if (cellCount > MaxDpCells)
        {
            return DiffLargeInputFallback(original, corrected);
        }

        var dp = new int[original.Count + 1, corrected.Count + 1];

        for (int i = original.Count - 1; i >= 0; i--)
        {
            for (int j = corrected.Count - 1; j >= 0; j--)
            {
                if (original[i] == corrected[j])
                {
                    dp[i, j] = dp[i + 1, j + 1] + 1;
                }
                else
                {
                    dp[i, j] = Math.Max(dp[i + 1, j], dp[i, j + 1]);
                }
            }
        }

        var ops = new List<DiffOp>();
        int x = 0;
        int y = 0;
        while (x < original.Count && y < corrected.Count)
        {
            if (original[x] == corrected[y])
            {
                ops.Add(new DiffOp(DiffKind.Equal, original[x]));
                x++;
                y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                ops.Add(new DiffOp(DiffKind.Delete, original[x]));
                x++;
            }
            else
            {
                ops.Add(new DiffOp(DiffKind.Insert, corrected[y]));
                y++;
            }
        }

        while (x < original.Count)
        {
            ops.Add(new DiffOp(DiffKind.Delete, original[x]));
            x++;
        }

        while (y < corrected.Count)
        {
            ops.Add(new DiffOp(DiffKind.Insert, corrected[y]));
            y++;
        }

        return ops;
    }

    private static List<DiffOp> DiffLargeInputFallback(IReadOnlyList<string> original, IReadOnlyList<string> corrected)
    {
        var prefix = 0;
        while (prefix < original.Count && prefix < corrected.Count && original[prefix] == corrected[prefix])
        {
            prefix++;
        }

        var originalSuffix = original.Count - 1;
        var correctedSuffix = corrected.Count - 1;
        while (originalSuffix >= prefix && correctedSuffix >= prefix && original[originalSuffix] == corrected[correctedSuffix])
        {
            originalSuffix--;
            correctedSuffix--;
        }

        var ops = new List<DiffOp>(original.Count + corrected.Count);
        for (var i = 0; i < prefix; i++)
        {
            ops.Add(new DiffOp(DiffKind.Equal, original[i]));
        }

        for (var i = prefix; i <= originalSuffix; i++)
        {
            ops.Add(new DiffOp(DiffKind.Delete, original[i]));
        }

        for (var i = prefix; i <= correctedSuffix; i++)
        {
            ops.Add(new DiffOp(DiffKind.Insert, corrected[i]));
        }

        for (var i = originalSuffix + 1; i < original.Count; i++)
        {
            ops.Add(new DiffOp(DiffKind.Equal, original[i]));
        }

        return ops;
    }

    public static List<DiffOp> MergeAdjacent(List<DiffOp> ops)
    {
        if (ops.Count == 0)
        {
            return ops;
        }

        var merged = new List<DiffOp> { ops[0] };
        for (int i = 1; i < ops.Count; i++)
        {
            var current = ops[i];
            var last = merged[^1];
            if (current.Kind == last.Kind)
            {
                merged[^1] = new DiffOp(last.Kind, last.Token + current.Token);
            }
            else
            {
                merged.Add(current);
            }
        }

        return merged;
    }

    private static void FlushToken(StringBuilder builder, List<string> tokens)
    {
        if (builder.Length == 0)
        {
            return;
        }

        tokens.Add(builder.ToString());
        builder.Clear();
    }

    private enum TokenMode
    {
        None,
        Word,
        Space
    }
}
