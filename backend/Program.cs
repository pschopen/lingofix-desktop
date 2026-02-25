using System.Text.Json;
using Lingofix.Backend.Documents;

namespace Lingofix.Backend;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        try
        {
            var (input, settingsPath, inspectTrackChanges, acceptExistingTrackChanges) = ParseArgs(args);
            if (string.IsNullOrWhiteSpace(input))
            {
                EmitError("Missing --input argument");
                return 1;
            }

            if (!File.Exists(input))
            {
                EmitError($"File not found: {input}");
                return 1;
            }

            if (inspectTrackChanges)
            {
                var hasTrackChanges = TrackChangesGenerator.ContainsTrackedChanges(input);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    type = "track_changes_inspection",
                    hasTrackChanges
                }));
                return 0;
            }

            var settings = await LoadSettings(settingsPath);
            var logger = new JsonLogger();
            var result = await LingofixRunner.RunAsync(
                new RunOptions(
                    input,
                    settings,
                    Settings.NormalizeCompareMode(settings.CompareMode),
                    acceptExistingTrackChanges),
                logger,
                cts.Token);

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                type = "result",
                success = true,
                outputPath = result.OutputPath,
                trackChangesCreated = result.TrackChangesCreated
            }));

            return 0;
        }
        catch (OperationCanceledException)
        {
            EmitError("Operation cancelled");
            return 1;
        }
        catch (Exception ex)
        {
            EmitError(ex.Message);
            return 1;
        }
    }

    private static async Task<Settings> LoadSettings(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new InvalidOperationException("Settings file is missing. Open Settings > Advanced and use 'Reset app'.");
        }

        var text = await File.ReadAllTextAsync(path);
        return Settings.FromFrontendJson(text);
    }

    private static (string? input, string? settingsPath, bool inspectTrackChanges, bool acceptExistingTrackChanges) ParseArgs(string[] args)
    {
        string? input = null;
        string? settingsPath = null;
        var inspectTrackChanges = false;
        var acceptExistingTrackChanges = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--input" && i + 1 < args.Length)
            {
                input = args[++i];
                continue;
            }

            if (arg == "--settings-path" && i + 1 < args.Length)
            {
                settingsPath = args[++i];
                continue;
            }

            if (arg == "--inspect-track-changes")
            {
                inspectTrackChanges = true;
                continue;
            }

            if (arg == "--accept-existing-track-changes")
            {
                acceptExistingTrackChanges = true;
            }
        }

        return (input, settingsPath, inspectTrackChanges, acceptExistingTrackChanges);
    }

    private static void EmitError(string message)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { type = "error", message }));
    }
}

internal sealed class JsonLogger : IRunLogger
{
    public void Info(string message)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { type = "log", level = "info", message }));
    }

    public void Warning(string message)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { type = "log", level = "warning", message }));
    }

    public void Error(string message)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { type = "log", level = "error", message }));
    }

    public void Progress(int percent, string message)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { type = "progress", percent, message }));
    }
}
