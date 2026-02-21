namespace Lingofix.Backend.Documents;

public static class PathUtils
{
    public static string NormalizeInputPath(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var trimmed = input.Trim();

        if ((trimmed.StartsWith("\"") && trimmed.EndsWith("\"")) || (trimmed.StartsWith("'") && trimmed.EndsWith("'")))
        {
            trimmed = trimmed.Substring(1, trimmed.Length - 2);
        }

        if (trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                trimmed = uri.LocalPath;
            }
        }

        trimmed = trimmed.Replace("\\ ", " ");
        return Path.GetFullPath(trimmed);
    }

    public static string BuildOutputPath(string inputPath, string suffix)
    {
        var directory = Path.GetDirectoryName(inputPath) ?? Directory.GetCurrentDirectory();
        var fileName = Path.GetFileNameWithoutExtension(inputPath);
        var ext = Path.GetExtension(inputPath);
        return Path.Combine(directory, $"{fileName}{suffix}{ext}");
    }

    public static string BuildTempCorrectedPath(string inputPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(inputPath);
        var ext = Path.GetExtension(inputPath);
        var tempDir = Path.Combine(Path.GetTempPath(), "Lingofix");
        Directory.CreateDirectory(tempDir);
        var unique = Guid.NewGuid().ToString("N");
        return Path.Combine(tempDir, $"{fileName}_corrected_{unique}{ext}");
    }

    public static string BuildTempOutputPath(string outputPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(outputPath);
        var ext = Path.GetExtension(outputPath);
        var tempDir = Path.Combine(Path.GetTempPath(), "Lingofix");
        Directory.CreateDirectory(tempDir);
        var unique = Guid.NewGuid().ToString("N");
        return Path.Combine(tempDir, $"{fileName}_output_{unique}{ext}");
    }

    public static void PromoteTempToFinal(string tempPath, string finalPath)
    {
        var finalDirectory = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrWhiteSpace(finalDirectory))
        {
            Directory.CreateDirectory(finalDirectory);
        }

        File.Move(tempPath, finalPath, overwrite: true);
    }

    public static string BuildCheckpointPath(string inputPath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Lingofix", "checkpoints");
        Directory.CreateDirectory(tempDir);
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(inputPath)))
            .ToLowerInvariant();
        return Path.Combine(tempDir, $"{key}.json");
    }
}
