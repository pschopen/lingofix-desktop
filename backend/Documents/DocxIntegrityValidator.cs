using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;

namespace Lingofix.Backend.Documents;

internal static class DocxIntegrityValidator
{
    private static readonly string[] DiffModeImmutableEntries =
    [
        "word/styles.xml",
        "word/numbering.xml",
        "word/fontTable.xml",
        "word/theme/theme1.xml"
    ];

    private static readonly string[] WordModeRequiredEntries =
    [
        "[Content_Types].xml",
        "_rels/.rels",
        "word/document.xml"
    ];

    public static IReadOnlyList<string> ImmutableEntries => DiffModeImmutableEntries;

    public static void Validate(string originalPath, string outputPath)
        => ValidateEntries(originalPath, outputPath, DiffModeImmutableEntries);

    public static void ValidateForWordCompare(string originalPath, string outputPath)
    {
        ValidateRequiredEntries(outputPath, WordModeRequiredEntries);
        ValidateOpenXmlStructure(outputPath);
    }

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

    private static void ValidateRequiredEntries(string outputPath, string[] requiredEntries)
    {
        using var output = ZipFile.OpenRead(outputPath);
        foreach (var entryName in requiredEntries)
        {
            if (output.GetEntry(entryName) is null)
            {
                throw new InvalidOperationException($"DOCX integrity check failed: missing required entry '{entryName}' in output.");
            }
        }
    }

    private static void ValidateOpenXmlStructure(string outputPath)
    {
        using var doc = WordprocessingDocument.Open(outputPath, false);
        if (doc.MainDocumentPart?.Document is null)
        {
            throw new InvalidOperationException("DOCX integrity check failed: output is missing main document part.");
        }

        if (doc.MainDocumentPart.Document.Body is null)
        {
            throw new InvalidOperationException("DOCX integrity check failed: output document body is missing.");
        }
    }
}
