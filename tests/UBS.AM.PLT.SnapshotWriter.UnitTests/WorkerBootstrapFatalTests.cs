using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace UBS.AM.PLT.SnapshotWriter.UnitTests;

/// <summary>
/// Regression guard for the fatal-startup path in <c>WorkerBootstrap</c> (the top-level
/// program). A startup failure (here: a blank required configuration key, which trips
/// ValidateOnStart) must:
///   - emit exactly ONE Critical, JSON-formatted, via the standalone fallback logger;
///   - NOT rethrow — the earlier bug rethrew after logging, so the exception escaped the
///     process, firing the AppDomain.UnhandledException hook (a duplicate Critical) and the
///     CLR default handler (a raw non-JSON stderr dump);
///   - still exit the process non-zero so k8s treats it as a crash and restarts.
/// The exact mistake previously found and fixed in KafkaSnapshotConsumer.ConsumeLoopAsync.
///
/// This is verified by launching the REAL built Worker as a genuine child process and
/// observing it from outside, rather than running WorkerBootstrap.RunAsync in-process.
/// RunAsync builds a real IHost with a background JSON-console logger and mutates
/// process-global state (Console.Out, Environment.ExitCode); doing that inside the shared
/// xunit test host — which runs many other tests in the same process — is unsafe and was
/// observed to hang/kill the whole test run. A child process isolates all of that and is
/// also the most faithful reproduction of what actually happens on a real crash.
/// </summary>
public class WorkerBootstrapFatalTests
{
    private static readonly TimeSpan ChildTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Fatal_startup_logs_exactly_one_Critical_json_does_not_rethrow_and_exits_non_zero()
    {
        var workerDll = LocateWorkerDll();
        var workerDir = Path.GetDirectoryName(workerDll)!;

        var startInfo = new ProcessStartInfo("dotnet")
        {
            // Run the real Worker entry point (Program.cs -> WorkerBootstrap.RunAsync).
            ArgumentList = { "exec", workerDll },
            // Content root = working directory, so the Worker loads its own appsettings.json
            // (valid Blob/Database config); we then blank one required key below to trip the
            // real ValidateOnStart fatal-startup path.
            WorkingDirectory = workerDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Force the fatal path: a blank required key fails ValidateOnStart at host start.
        startInfo.Environment["Kafka__BootstrapServers"] = string.Empty;
        // Keep logging config deterministic regardless of the developer's machine env.
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)ChildTimeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort — the assertion below reports the real failure.
            }

            Assert.Fail($"Worker child process did not exit within {ChildTimeout.TotalSeconds:0}s (hung startup).");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        // Non-zero exit so k8s restarts the pod. (WorkerBootstrap sets Environment.ExitCode = 1
        // on the fatal path and returns normally; that surfaces as the process exit code.)
        Assert.True(
            process.ExitCode != 0,
            $"Expected non-zero exit code, got {process.ExitCode}.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");

        var stdoutLines = SplitLines(stdout);
        var criticalLines = stdoutLines.Where(IsJsonCritical).ToArray();

        // Exactly one Critical, well-formed JSON (not a raw CLR stack-trace dump), naming the reason.
        Assert.True(
            criticalLines.Length == 1,
            $"Expected exactly one Critical JSON line, got {criticalLines.Length}.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        Assert.Contains("terminating during startup", criticalLines[0]);

        // The AppDomain safety-net hook must NOT have fired: that message only appears when the
        // exception escapes the process, i.e. when the old rethrow bug is present.
        var combined = stdout + "\n" + stderr;
        Assert.DoesNotContain("non-owned thread", combined, StringComparison.Ordinal);

        // No raw, non-JSON CLR unhandled-exception dump on stderr (the other symptom of a rethrow).
        Assert.DoesNotContain("Unhandled exception", combined, StringComparison.Ordinal);
    }

    private static string[] SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsJsonCritical(string line)
    {
        if (!line.StartsWith('{'))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("LogLevel", out var level)
                && level.GetString() == "Critical";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves the built Worker assembly from this test assembly's output directory. The test
    /// project references the Worker project, so the Worker is built to its own bin under the
    /// SAME configuration (Debug/Release) and target framework (e.g. net10.0) as these tests.
    /// </summary>
    private static string LocateWorkerDll()
    {
        const string workerAssembly = "UBS.AM.PLT.SnapshotWriter.Worker";

        var baseDir = new DirectoryInfo(AppContext.BaseDirectory);
        var tfm = baseDir.Name;                 // e.g. net10.0
        var configuration = baseDir.Parent!.Name; // e.g. Debug

        // Walk up to the repository (worktree) root: the directory that contains "src".
        var current = baseDir;
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "src")))
        {
            current = current.Parent;
        }

        Assert.True(current is not null, $"Could not locate repository root (a 'src' folder) above {AppContext.BaseDirectory}.");

        var dll = Path.Combine(
            current!.FullName,
            "src",
            workerAssembly,
            "bin",
            configuration,
            tfm,
            workerAssembly + ".dll");

        Assert.True(File.Exists(dll), $"Built Worker assembly not found at expected path: {dll}");
        return dll;
    }
}
