namespace Lingofix.Backend.Documents;

internal static class XmlTextSanitizer
{
    public static string StripInvalidXmlChars(string? text, out int removedChars)
    {
        removedChars = 0;
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        System.Text.StringBuilder? builder = null;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    var codePoint = char.ConvertToUtf32(ch, text[i + 1]);
                    if (IsAllowedXmlCodePoint(codePoint))
                    {
                        builder?.Append(ch);
                        builder?.Append(text[i + 1]);
                    }
                    else
                    {
                        EnsureBuilder(ref builder, text, i);
                        removedChars += 2;
                    }

                    i++;
                    continue;
                }

                EnsureBuilder(ref builder, text, i);
                removedChars++;
                continue;
            }

            if (char.IsLowSurrogate(ch))
            {
                EnsureBuilder(ref builder, text, i);
                removedChars++;
                continue;
            }

            if (IsAllowedXmlCodePoint(ch))
            {
                builder?.Append(ch);
                continue;
            }

            EnsureBuilder(ref builder, text, i);
            removedChars++;
        }

        return builder is null ? text : builder.ToString();
    }

    private static void EnsureBuilder(ref System.Text.StringBuilder? builder, string source, int copyUntil)
    {
        if (builder is not null)
        {
            return;
        }

        builder = new System.Text.StringBuilder(source.Length);
        if (copyUntil > 0)
        {
            builder.Append(source, 0, copyUntil);
        }
    }

    private static bool IsAllowedXmlCodePoint(int codePoint)
    {
        return codePoint == 0x9 ||
               codePoint == 0xA ||
               codePoint == 0xD ||
               (codePoint >= 0x20 && codePoint <= 0xD7FF) ||
               (codePoint >= 0xE000 && codePoint <= 0xFFFD) ||
               (codePoint >= 0x10000 && codePoint <= 0x10FFFF);
    }
}
