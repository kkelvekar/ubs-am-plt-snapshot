using Microsoft.Extensions.Logging.Console;
using UBS.AM.PLT.SnapshotWriter.Application;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Adls;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Kafka;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Sql;

namespace UBS.AM.PLT.SnapshotWriter.Worker;

/// <summary>
/// Host bootstrap for the Snapshot Writer worker, extracted from the top-level program so
/// the fatal-startup path is directly testable (top-level statements are not). The rule the
/// brief holds us to: any path that terminates the process emits exactly ONE structured,
/// JSON-formatted LogCritical (with reason) before termination.
/// </summary>
public static class WorkerBootstrap
{
    public static async Task RunAsync(string[] args)
    {
        try
        {
            var builder = Host.CreateApplicationBuilder(args);

            // Consumer death must kill the pod so Kubernetes restarts it — never a silently-dead
            // worker that stays Running while consuming nothing.
            builder.Services.Configure<HostOptions>(options =>
                options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost);

            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services
                .AddApplication()
                .AddSqlInfrastructure(builder.Configuration)
                .AddAdlsInfrastructure(builder.Configuration)
                .AddKafkaInfrastructure(builder.Configuration)
                .AddSnapshotConfigInfrastructure(builder.Configuration);

            var host = builder.Build();

            // Last-resort hook for exceptions on threads we don't own (e.g. third-party callback
            // threads) that would otherwise crash the process with no structured signal. This is
            // a safety net for failure modes RunAsync's own try/catch can never see; it must NOT
            // fire for exceptions this method already handles below.
            var appDomainLogger = host.Services
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("UBS.AM.PLT.SnapshotWriter.Worker");
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                appDomainLogger.LogCritical(
                    e.ExceptionObject as Exception,
                    "Unhandled exception on a non-owned thread; isTerminating={IsTerminating}",
                    e.IsTerminating);

            await host.RunAsync();
        }
        catch (OperationCanceledException)
        {
            // Normal host shutdown (SIGTERM / Ctrl+C) surfaces as cancellation — not a failure.
        }
        catch (Exception ex)
        {
            // Startup/runtime failure before or outside host logging (bad config binding, options
            // ValidateOnStart, DI wiring, host.Run throw). The host may be the thing that failed to
            // build, so log through a standalone JSON-console LoggerFactory rather than host logging
            // — this also satisfies the no-Console.WriteLine rule. One Critical, flush, exit non-zero.
            using var fallbackLoggerFactory = LoggerFactory.Create(logging =>
                logging.AddJsonConsole(options => options.UseUtcTimestamp = true));
            fallbackLoggerFactory
                .CreateLogger("UBS.AM.PLT.SnapshotWriter.Worker")
                .LogCritical(ex, "Snapshot Writer worker is terminating during startup.");

            // Do NOT rethrow. This exception has already been fully handled and logged with our
            // single Critical. Rethrowing would let it escape the process, firing both the
            // AppDomain.UnhandledException hook above (a duplicate Critical) and the CLR's default
            // unhandled-exception handler (a raw, non-JSON stderr stack-trace dump) — the exact
            // duplicate-logging mistake already fixed in KafkaSnapshotConsumer.ConsumeLoopAsync.
            // Environment.ExitCode = 1 (not a value returned from Main) still surfaces as the
            // process exit code when the top-level program returns normally, so k8s sees a
            // crash-like exit and restarts the pod — with clean single-line JSON logging.
            Environment.ExitCode = 1;
        }
    }
}
