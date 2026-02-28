using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Lingofix.Backend.Documents;

internal static class PandocMarkdownConverter
{
    private const string PandocPathEnv = "LINGOFIX_PANDOC_PATH";
    private static readonly TimeSpan PandocTimeout = TimeSpan.FromMinutes(15);

    public static void ConvertDocxToMarkdown(string inputDocxPath, string outputMarkdownPath)
    {
        EnsureInputFile(inputDocxPath, ".docx");
        EnsureParentDirectory(outputMarkdownPath);
        RunPandoc(
            [
                inputDocxPath,
                "--from",
                "docx",
                "--to",
                "markdown",
                "--output",
                outputMarkdownPath
            ],
            "DOCX to Markdown conversion failed");
    }

    public static void ConvertMarkdownToDocx(string inputMarkdownPath, string outputDocxPath, string referenceDocxPath)
    {
        EnsureInputFile(inputMarkdownPath, ".md");
        EnsureInputFile(referenceDocxPath, ".docx");
        EnsureParentDirectory(outputDocxPath);
        RunPandoc(
            [
                inputMarkdownPath,
                "--from",
                "markdown+footnotes",
                "--to",
                "docx",
                "--reference-doc",
                referenceDocxPath,
                "--output",
                outputDocxPath
            ],
            "Markdown to DOCX conversion failed");
    }

    public static void ConvertMarkdownToPandocJson(string inputMarkdownPath, string outputJsonPath)
    {
        EnsureInputFile(inputMarkdownPath, ".md");
        EnsureParentDirectory(outputJsonPath);
        RunPandoc(
            [
                inputMarkdownPath,
                "--from",
                "markdown+footnotes",
                "--to",
                "json",
                "--output",
                outputJsonPath
            ],
            "Markdown to Pandoc JSON conversion failed");
    }

    public static void ConvertPandocJsonToMarkdown(string inputJsonPath, string outputMarkdownPath)
    {
        EnsureInputFile(inputJsonPath, ".json");
        EnsureParentDirectory(outputMarkdownPath);
        RunPandoc(
            [
                inputJsonPath,
                "--from",
                "json",
                "--to",
                "markdown+footnotes",
                "--output",
                outputMarkdownPath
            ],
            "Pandoc JSON to Markdown conversion failed");
    }

    private static void EnsureInputFile(string path, string expectedExtension)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path is missing.", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"File not found: {path}", path);
        }

        if (!path.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Expected {expectedExtension} file: {path}");
        }
    }

    private static void EnsureParentDirectory(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("Output path is missing.", nameof(outputPath));
        }

        var parent = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }

    private static void RunPandoc(IReadOnlyList<string> args, string operation)
    {
        var pandocPath = ResolvePandocExecutable();
        var psi = new ProcessStartInfo
        {
            FileName = pandocPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi);
        if (process is null)
        {
            throw new InvalidOperationException($"{operation}: failed to start pandoc process.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)PandocTimeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw new TimeoutException($"{operation}: pandoc timed out after {PandocTimeout.TotalSeconds:0} seconds.");
        }

        Task.WaitAll([stdoutTask, stderrTask]);

        if (process.ExitCode == 0)
        {
            return;
        }

        var stderr = stderrTask.GetAwaiter().GetResult().Trim();
        var stdout = stdoutTask.GetAwaiter().GetResult().Trim();
        var details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        if (string.IsNullOrWhiteSpace(details))
        {
            details = $"pandoc exited with status {process.ExitCode}";
        }

        throw new InvalidOperationException($"{operation}: {details}");
    }

    private static string ResolvePandocExecutable()
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var candidate = value.Trim();
            if (seen.Add(candidate))
            {
                candidates.Add(candidate);
            }
        }

        AddCandidate(Environment.GetEnvironmentVariable(PandocPathEnv));

        var baseDir = AppContext.BaseDirectory;
        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "pandoc.exe" : "pandoc";
        AddCandidate(Path.Combine(baseDir, exeName));
        AddCandidate(Path.Combine(baseDir, "binaries", exeName));

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            AddCandidate("pandoc.exe");
            AddCandidate("C:/Program Files/Pandoc/pandoc.exe");
        }
        else
        {
            AddCandidate("pandoc");
            AddCandidate("/opt/homebrew/bin/pandoc");
            AddCandidate("/usr/local/bin/pandoc");
            AddCandidate("/usr/bin/pandoc");
        }

        var failures = new List<string>();
        foreach (var candidate in candidates)
        {
            if (Path.IsPathFullyQualified(candidate) && !File.Exists(candidate))
            {
                failures.Add($"{candidate}: file not found");
                continue;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = candidate,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                psi.ArgumentList.Add("--version");

                using var process = Process.Start(psi);
                if (process is null)
                {
                    failures.Add($"{candidate}: process could not be started");
                    continue;
                }

                if (!process.WaitForExit(8000))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                    }

                    failures.Add($"{candidate}: probe timed out");
                    continue;
                }

                if (process.ExitCode == 0)
                {
                    return candidate;
                }

                var stderr = process.StandardError.ReadToEnd().Trim();
                var stdout = process.StandardOutput.ReadToEnd().Trim();
                failures.Add(string.IsNullOrWhiteSpace(stderr) && string.IsNullOrWhiteSpace(stdout)
                    ? $"{candidate}: exited with {process.ExitCode}"
                    : $"{candidate}: {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr)}");
            }
            catch (Exception ex)
            {
                failures.Add($"{candidate}: {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            "Could not locate a usable pandoc executable. " +
            $"Set {PandocPathEnv} to the pandoc executable path if needed.\n\n" +
            $"Details:\n{string.Join(Environment.NewLine, failures)}");
    }
}
