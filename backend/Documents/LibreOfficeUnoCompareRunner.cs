using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Lingofix.Backend.Documents;

internal static class LibreOfficeUnoCompareRunner
{
    private const string LibreOfficePythonEnv = "LINGOFIX_LIBREOFFICE_PYTHON";

    public static void GenerateWithUno(
        string sofficePath,
        string originalPath,
        string correctedPath,
        string outputPath,
        string author,
        TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(sofficePath))
        {
            throw new ArgumentException("Missing soffice executable path.", nameof(sofficePath));
        }

        var pythonPath = ResolveLibreOfficePythonExecutable(sofficePath);
        var scriptPath = ResolveCompareScriptPath();

        var workspace = Path.Combine(Path.GetTempPath(), "Lingofix", "libreoffice-uno", Guid.NewGuid().ToString("N"));
        var userProfilePath = Path.Combine(workspace, "profile");
        Directory.CreateDirectory(userProfilePath);

        var port = ReserveFreePort();
        var userProfileUri = new Uri(userProfilePath).AbsoluteUri;
        var acceptArg = $"socket,host=127.0.0.1,port={port};urp;StarOffice.ComponentContext";

        Process? sofficeProcess = null;
        try
        {
            var sofficePsi = new ProcessStartInfo
            {
                FileName = sofficePath,
                UseShellExecute = false,
                CreateNoWindow = true
            };
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
                ["compare", "127.0.0.1", port.ToString(), originalPath, correctedPath, outputPath, author]);

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
            TryDeleteDirectory(workspace);
        }
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
        var psi = new ProcessStartInfo
        {
            FileName = pythonPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

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
                var psi = new ProcessStartInfo
                {
                    FileName = candidate,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
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

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
        }
    }
}
