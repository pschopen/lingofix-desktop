using System.IO.Compression;

namespace Lingofix.Backend.Documents;

internal static class DocxIntegrityValidator
{
    private static readonly string[] StrictImmutableEntries =
    [
        "word/styles.xml",
        "word/numbering.xml",
        "word/fontTable.xml",
        "word/theme/theme1.xml",
        "word/comments.xml"
    ];

    public static IReadOnlyList<string> ImmutableEntries => StrictImmutableEntries;

    public static void Validate(string originalPath, string outputPath)
        => ValidateEntries(originalPath, outputPath, StrictImmutableEntries);

    public static void ValidateForWordCompare(string originalPath, string outputPath)
        => ValidateEntries(originalPath, outputPath, StrictImmutableEntries);

    private static void ValidateEntries(string originalPath, string outputPath, string[] immutableEntries)
    {
        using var original = ZipFile.OpenRead(originalPath);
        using var output = ZipFile.OpenRead(outputPath);

        foreach (var entryName in immutableEntries)
        {
            var originalEntry = original.GetEntry(entryName);
            if (originalEntry is null)
            {
                continue;
            }

            var outputEntry = output.GetEntry(entryName);
            if (outputEntry is null)
            {
                throw new InvalidOperationException($"DOCX integrity check failed: missing required entry '{entryName}' in output.");
            }

            var originalBytes = ReadAllBytes(originalEntry);
            var outputBytes = ReadAllBytes(outputEntry);
            if (!originalBytes.AsSpan().SequenceEqual(outputBytes))
            {
                throw new InvalidOperationException($"DOCX integrity check failed: immutable entry changed '{entryName}'.");
            }
        }
    }

    private static byte[] ReadAllBytes(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
