using UBS.AM.PLT.SnapshotWriter.Worker;

// Thin entry point: all bootstrap logic (including the single-Critical fatal-startup path)
// lives in WorkerBootstrap so it is testable. No rethrow here — RunAsync sets
// Environment.ExitCode on the fatal path and returns; returning normally lets that exit code
// surface as the process exit code.
await WorkerBootstrap.RunAsync(args);
