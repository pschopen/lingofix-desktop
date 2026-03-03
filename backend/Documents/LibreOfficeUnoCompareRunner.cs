using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Lingofix.Backend.Documents;

internal static class LibreOfficeUnoCompareRunner
{
    private const string LibreOfficePythonEnv = "LINGOFIX_LIBREOFFICE_PYTHON";
    private const string FlatpakIdEnv = "FLATPAK_ID";

    public static void GenerateWithUno(
        string sofficePath,
        string originalPath,
        string correctedPath,
        string outputPath,
        string author,
        TimeSpan timeout,
        string changeFilterMode = "all")
    {
        if (string.IsNullOrWhiteSpace(sofficePath))
        {
            throw new ArgumentException("Missing soffice executable path.", nameof(sofficePath));
        }

        var effectiveSofficePath = ResolveSofficePathForHeadlessLaunch(sofficePath);
        var pythonPath = ResolveLibreOfficePythonExecutable(effectiveSofficePath);
        var scriptPath = PrepareCompareScriptForExecution(ResolveCompareScriptPath());

        var workspace = Path.Combine(PathUtils.GetLingofixTempRoot(), "libreoffice-uno", Guid.NewGuid().ToString("N"));
        var userProfilePath = Path.Combine(workspace, "profile");
        Directory.CreateDirectory(userProfilePath);

        var compareOriginalPath = originalPath;
        var compareCorrectedPath = correctedPath;
        var outputFormat = string.Equals(Path.GetExtension(outputPath), ".odt", StringComparison.OrdinalIgnoreCase)
            ? "odt"
            : "docx";
        var effectiveChangeFilterMode = string.Equals(changeFilterMode, "text-only", StringComparison.OrdinalIgnoreCase)
            ? "text-only"
            : "all";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            string.Equals(Path.GetExtension(originalPath), ".docx", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetExtension(correctedPath), ".docx", StringComparison.OrdinalIgnoreCase))
        {
            var normalizedDir = Path.Combine(workspace, "normalized");
            Directory.CreateDirectory(normalizedDir);
            compareOriginalPath = RoundTripDocxForCompare(effectiveSofficePath, originalPath, normalizedDir, timeout);
            compareCorrectedPath = RoundTripDocxForCompare(effectiveSofficePath, correctedPath, normalizedDir, timeout);
        }

        var port = ReserveFreePort();
        var userProfileUri = new Uri(userProfilePath).AbsoluteUri;
        var acceptArg = $"socket,host=127.0.0.1,port={port};urp;StarOffice.ComponentContext";

        Process? sofficeProcess = null;
        try
        {
            var sofficePsi = CreateHostAwareProcessStartInfo(effectiveSofficePath, redirectOutput: false);
            sofficePsi.ArgumentList.Add("--headless");
            sofficePsi.ArgumentList.Add("--invisible");
            sofficePsi.ArgumentList.Add("--norestore");
            sofficePsi.ArgumentList.Add("--nolockcheck");
            sofficePsi.ArgumentList.Add("--nodefault");
            sofficePsi.ArgumentList.Add($"-env:UserInstallation={userProfileUri}");
            sofficePsi.ArgumentList.Add($"--accept={acceptArg}");

            sofficeProcess = Process.Start(sofficePsi);
            if (sofficeProcess is null)
            {
                throw new InvalidOperationException("Failed to start LibreOffice (soffice). Process could not be started.");
            }

            WaitForUnoBridge(pythonPath, scriptPath, port, timeout, sofficeProcess);

            var (_, compareError, compareExitCode) = RunPythonCommand(
                pythonPath,
                scriptPath,
                timeout,
                ["compare", "127.0.0.1", port.ToString(), compareOriginalPath, compareCorrectedPath, outputPath, author, outputFormat, effectiveChangeFilterMode]);

            if (compareExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"LibreOffice UNO compare failed (exit {compareExitCode}). {TrimForError(compareError)}");
            }

            if (!File.Exists(outputPath))
            {
                throw new FileNotFoundException(
                    $"LibreOffice UNO compare did not create output file: {outputPath}",
                    outputPath);
            }
        }
        finally
        {
            TryStopProcess(sofficeProcess);
        }
    }

    private static string RoundTripDocxForCompare(string sofficePath, string inputPath, string outputDir, TimeSpan timeout)
    {
        Directory.CreateDirectory(outputDir);

        var expectedOutput = Path.Combine(
            outputDir,
            Path.GetFileNameWithoutExtension(inputPath) + ".docx");

        var psi = CreateHostAwareProcessStartInfo(sofficePath, redirectOutput: true);

        psi.ArgumentList.Add("--headless");
        psi.ArgumentList.Add("--convert-to");
        psi.ArgumentList.Add("docx");
        psi.ArgumentList.Add("--outdir");
        psi.ArgumentList.Add(outputDir);
        psi.ArgumentList.Add(inputPath);

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            throw new InvalidOperationException($"Failed to start LibreOffice conversion process ({sofficePath}).");
        }

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw new TimeoutException($"LibreOffice DOCX round-trip conversion timed out after {timeout.TotalSeconds:0} seconds.");
        }

        Task.WaitAll([stdoutTask, stderrTask]);
        if (proc.ExitCode != 0)
        {
            var stderr = stderrTask.GetAwaiter().GetResult();
            var stdout = stdoutTask.GetAwaiter().GetResult();
            var details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"LibreOffice DOCX round-trip conversion failed: {TrimForError(details)}");
        }

        if (!File.Exists(expectedOutput))
        {
            throw new FileNotFoundException(
                $"LibreOffice DOCX round-trip conversion did not produce expected file: {expectedOutput}",
                expectedOutput);
        }

        return expectedOutput;
    }

    private static string ResolveSofficePathForHeadlessLaunch(string sofficePath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return sofficePath;
        }

        var trimmed = sofficePath.Trim();
        var normalized = trimmed.Replace('\\', '/');
        if (normalized.EndsWith("/soffice.com", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("soffice.com", StringComparison.OrdinalIgnoreCase))
        {
            var exeCandidate = trimmed[..^3] + "exe";
            if (!Path.IsPathFullyQualified(exeCandidate) || File.Exists(exeCandidate))
            {
                return exeCandidate;
            }
        }

        return trimmed;
    }

    private static void WaitForUnoBridge(string pythonPath, string scriptPath, int port, TimeSpan timeout, Process sofficeProcess)
    {
        var start = DateTime.UtcNow;
        var lastError = string.Empty;

        while (DateTime.UtcNow - start < timeout)
        {
            if (sofficeProcess.HasExited)
            {
                throw new InvalidOperationException($"LibreOffice (soffice) exited unexpectedly with code {sofficeProcess.ExitCode}.");
            }

            var (_, stderr, exitCode) = RunPythonCommand(
                pythonPath,
                scriptPath,
                TimeSpan.FromSeconds(8),
                ["probe", "127.0.0.1", port.ToString()]);

            if (exitCode == 0)
            {
                return;
            }

            lastError = TrimForError(stderr);
            if (lastError.Contains("No module named 'uno'", StringComparison.OrdinalIgnoreCase) ||
                lastError.Contains("No module named uno", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            Thread.Sleep(500);
        }

        throw new TimeoutException(
            $"Timed out waiting for LibreOffice UNO bridge. Last error: {lastError}");
    }

    private static (string stdout, string stderr, int exitCode) RunPythonCommand(
        string pythonPath,
        string scriptPath,
        TimeSpan timeout,
        IReadOnlyList<string> args)
    {
        var psi = CreateHostAwareProcessStartInfo(pythonPath, redirectOutput: true);

        psi.ArgumentList.Add(scriptPath);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            throw new InvalidOperationException($"Failed to start LibreOffice Python runtime: {pythonPath}");
        }

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw new TimeoutException($"LibreOffice Python helper timed out after {timeout.TotalSeconds:0} seconds.");
        }

        Task.WaitAll([stdoutTask, stderrTask]);
        return (
            stdoutTask.GetAwaiter().GetResult(),
            stderrTask.GetAwaiter().GetResult(),
            proc.ExitCode);
    }

    private static string ResolveCompareScriptPath()
    {
        var searchPaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "libreoffice-compare.py"),
            Path.Combine(AppContext.BaseDirectory, "binaries", "libreoffice-compare.py"),
            Path.Combine(Directory.GetCurrentDirectory(), "libreoffice-compare.py"),
            Path.Combine(Directory.GetCurrentDirectory(), "binaries", "libreoffice-compare.py"),
            Path.Combine(AppContext.BaseDirectory, "..", "Resources", "libreoffice-compare.py"),
            Path.Combine(AppContext.BaseDirectory, "..", "Resources", "binaries", "libreoffice-compare.py"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "Resources", "libreoffice-compare.py"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "Resources", "binaries", "libreoffice-compare.py")
        };

        foreach (var path in searchPaths.Select(Path.GetFullPath))
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException(
            "libreoffice-compare.py not found in expected resource locations.");
    }

    private static string PrepareCompareScriptForExecution(string scriptPath)
    {
        if (!IsFlatpakSandbox())
        {
            return scriptPath;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "Lingofix", "flatpak-host-scripts");
        Directory.CreateDirectory(tempDir);

        var targetPath = Path.Combine(
            tempDir,
            $"libreoffice-compare-{Guid.NewGuid():N}.py");
        File.Copy(scriptPath, targetPath, overwrite: true);
        return targetPath;
    }

    private static string ResolveLibreOfficePythonExecutable(string sofficePath)
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

        AddCandidate(Environment.GetEnvironmentVariable(LibreOfficePythonEnv));

        var sofficeDir = Path.GetDirectoryName(sofficePath) ?? string.Empty;
        var sofficeDirResolved = string.IsNullOrWhiteSpace(sofficeDir)
            ? string.Empty
            : Path.GetFullPath(sofficeDir);

        if (!string.IsNullOrWhiteSpace(sofficeDirResolved))
        {
            AddCandidate(Path.Combine(sofficeDirResolved, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "python.exe" : "python"));
            AddCandidate(Path.Combine(sofficeDirResolved, "python3"));

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var contentsDir = Directory.GetParent(sofficeDirResolved)?.FullName;
                if (!string.IsNullOrWhiteSpace(contentsDir))
                {
                    AddCandidate(Path.Combine(contentsDir, "Resources", "python"));
                    AddCandidate(Path.Combine(contentsDir, "program", "python"));
                }
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            AddCandidate("C:/Program Files/LibreOffice/program/python.exe");
            AddCandidate("C:/Program Files (x86)/LibreOffice/program/python.exe");
            AddCandidate("python.exe");
        }
        else
        {
            if (IsFlatpakSandbox())
            {
                AddCandidate("/run/host/usr/lib64/libreoffice/program/python");
                AddCandidate("/run/host/usr/lib/libreoffice/program/python");
            }
            AddCandidate("/usr/lib/libreoffice/program/python");
            AddCandidate("/usr/lib64/libreoffice/program/python");
            AddCandidate("python3");
            AddCandidate("python");
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
                var psi = CreateHostAwareProcessStartInfo(candidate, redirectOutput: true);
                psi.ArgumentList.Add("--version");

                using var proc = Process.Start(psi);
                if (proc is null)
                {
                    failures.Add($"{candidate}: process could not be started");
                    continue;
                }

                if (!proc.WaitForExit(10000))
                {
                    try
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                    }

                    failures.Add($"{candidate}: probe timed out");
                    continue;
                }

                if (proc.ExitCode == 0)
                {
                    return candidate;
                }

                var stderr = proc.StandardError.ReadToEnd().Trim();
                var stdout = proc.StandardOutput.ReadToEnd().Trim();
                failures.Add(string.IsNullOrWhiteSpace(stderr) && string.IsNullOrWhiteSpace(stdout)
                    ? $"{candidate}: exited with {proc.ExitCode}"
                    : $"{candidate}: {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr)}");
            }
            catch (Exception ex)
            {
                failures.Add($"{candidate}: {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            "Could not locate a usable LibreOffice Python runtime (pyuno). " +
            $"Set {LibreOfficePythonEnv} to the LibreOffice python executable if needed.\n\n" +
            $"Details:\n{string.Join(Environment.NewLine, failures)}");
    }

    private static bool IsFlatpakSandbox()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(FlatpakIdEnv));

    private static string ResolveFlatpakHostExecutable(string executable)
    {
        if (!IsFlatpakSandbox())
        {
            return executable;
        }

        const string hostPrefix = "/run/host";
        if (executable.StartsWith(hostPrefix, StringComparison.Ordinal))
        {
            var hostPath = executable[hostPrefix.Length..];
            return string.IsNullOrWhiteSpace(hostPath) ? executable : hostPath;
        }

        return executable;
    }

    private static ProcessStartInfo CreateHostAwareProcessStartInfo(string executable, bool redirectOutput)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        if (IsFlatpakSandbox())
        {
            psi.FileName = "flatpak-spawn";
            psi.ArgumentList.Add("--host");
            psi.ArgumentList.Add(ResolveFlatpakHostExecutable(executable));
            return psi;
        }

        psi.FileName = executable;
        var workingDirectory = Path.GetDirectoryName(executable);
        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
        {
            psi.WorkingDirectory = workingDirectory;
        }

        return psi;
    }

    private static int ReserveFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string TrimForError(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "No details available.";
        }

        var trimmed = value.Trim();
        if (trimmed.Length <= 2000)
        {
            return trimmed;
        }

        return trimmed[..2000] + "...";
    }

    private static void TryStopProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

}
