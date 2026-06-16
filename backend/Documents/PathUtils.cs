namespace Lingofix.Backend.Documents;

public static class PathUtils
{
    public const string TempDirectoryEnv = "LINGOFIX_TEMP_DIR";

    public static string GetLingofixTempRoot()
    {
        var fromEnv = Environment.GetEnvironmentVariable(TempDirectoryEnv)?.Trim();
        var root = string.IsNullOrWhiteSpace(fromEnv)
            ? GetDefaultTempRoot()
            : Path.GetFullPath(fromEnv);
        Directory.CreateDirectory(root);
        return root;
    }

    private static string GetDefaultTempRoot()
    {
        var inFlatpak = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLATPAK_ID"));
        if (inFlatpak)
        {
            var cacheHome = Environment.GetEnvironmentVariable("XDG_CACHE_HOME")?.Trim();
            if (!string.IsNullOrWhiteSpace(cacheHome))
            {
                return Path.Combine(Path.GetFullPath(cacheHome), "Lingofix");
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                return Path.Combine(home, ".cache", "Lingofix");
            }
        }

        return Path.Combine(Path.GetTempPath(), "Lingofix");
    }

    private static string ComputeInputKey(string inputPath)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(inputPath));
        return Convert.ToHexString(hash).ToLowerInvariant().Substring(0, 12);
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "document";
        }

        var sanitized = name;
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(c, '_');
        }

        foreach (var c in new[] { ':', '*', '?', '"', '<', '>', '|' })
        {
            sanitized = sanitized.Replace(c, '_');
        }

        const int maxLength = 100;
        return sanitized.Length > maxLength ? sanitized.Substring(0, maxLength) : sanitized;
    }

    public static string BuildWordCompareWorkspace(string inputPath)
    {
        var workspace = Path.Combine(GetLingofixTempRoot(), "compare");
        Directory.CreateDirectory(workspace);
        return workspace;
    }

    public static string BuildWordCompareFilePath(string inputPath, string fileName)
    {
        var key = ComputeInputKey(inputPath);
        var stem = SanitizeFileName(Path.GetFileNameWithoutExtension(inputPath));
        var role = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        return Path.Combine(BuildWordCompareWorkspace(inputPath), $"{key}_{stem}_{role}{ext}");
    }

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
        var tempDir = GetLingofixTempRoot();
        Directory.CreateDirectory(tempDir);
        var unique = Guid.NewGuid().ToString("N");
        return Path.Combine(tempDir, $"{fileName}_corrected_{unique}{ext}");
    }

    public static string BuildTempOutputPath(string outputPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(outputPath);
        var ext = Path.GetExtension(outputPath);
        var tempDir = GetLingofixTempRoot();
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
        var tempDir = Path.Combine(GetLingofixTempRoot(), "checkpoints");
        Directory.CreateDirectory(tempDir);
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(inputPath)))
            .ToLowerInvariant();
        return Path.Combine(tempDir, $"{key}.json");
    }
}
