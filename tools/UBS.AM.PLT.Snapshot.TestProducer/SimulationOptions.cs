namespace UBS.AM.PLT.Snapshot.TestProducer;

public sealed record SimulationOptions(
    string BootstrapServers = "localhost:9092",
    string Topic = "ubs-advantage-snapshots",
    int SnapshotCount = 1,
    TimeSpan? MessageDelayOverride = null,
    TimeSpan? SnapshotDelayMinOverride = null,
    TimeSpan? SnapshotDelayMaxOverride = null,
    string? DataFilePathOverride = null,
    bool ShowHelp = false)
{
    public TimeSpan MessageDelay => MessageDelayOverride ?? TimeSpan.FromSeconds(30);

    public TimeSpan SnapshotDelayMin => SnapshotDelayMinOverride ?? TimeSpan.FromMinutes(1);

    public TimeSpan SnapshotDelayMax => SnapshotDelayMaxOverride ?? TimeSpan.FromMinutes(2);

    public string DataFilePath => DataFilePathOverride ?? Path.Combine(AppContext.BaseDirectory, "Data", "snapshot-simulation-data.json");

    public static SimulationOptions Default { get; } = new();

    public static string HelpText =>
        """
        Publishes simulated portfolio snapshot payloads to Kafka.

        Usage:
          dotnet run --project tools/UBS.AM.PLT.Snapshot.TestProducer -- [bootstrapServers] [options]

        Options:
          --bootstrap-servers <value>      Kafka bootstrap servers. Defaults to Kafka__BootstrapServers or localhost:9092.
          --topic <value>                  Kafka topic. Defaults to Kafka__Topics__snapshot-request or ubs-advantage-snapshots.
          --snapshots <number>             Number of snapshots to generate. Each snapshot sends 4 payload messages.
          --message-delay <hh:mm:ss>       Delay between payloads in the same snapshot. Defaults to 00:00:30.
          --snapshot-delay-min <hh:mm:ss>  Minimum delay between snapshots. Defaults to 00:01:00.
          --snapshot-delay-max <hh:mm:ss>  Maximum delay between snapshots. Defaults to 00:02:00.
          --data-file <path>               JSON simulation data file.
          --help                           Show this help.

        Quick local smoke test:
          dotnet run --project tools/UBS.AM.PLT.Snapshot.TestProducer -- --snapshots 1 --message-delay 00:00:00
        """;

    public static SimulationOptions Parse(string[] args, Func<string, string?> environment)
    {
        var bootstrapServers = environment("Kafka__BootstrapServers") ?? Default.BootstrapServers;
        var topic = environment("Kafka__Topics__snapshot-request") ?? Default.Topic;
        var snapshotCount = Default.SnapshotCount;
        TimeSpan? messageDelay = null;
        TimeSpan? snapshotDelayMin = null;
        TimeSpan? snapshotDelayMax = null;
        string? dataFilePath = null;
        var showHelp = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--help" or "-h":
                    showHelp = true;
                    break;
                case "--bootstrap-servers":
                    bootstrapServers = ReadValue(args, ref i, arg);
                    break;
                case "--topic":
                    topic = ReadValue(args, ref i, arg);
                    break;
                case "--snapshots":
                    snapshotCount = int.Parse(ReadValue(args, ref i, arg));
                    break;
                case "--message-delay":
                    messageDelay = TimeSpan.Parse(ReadValue(args, ref i, arg));
                    break;
                case "--snapshot-delay-min":
                    snapshotDelayMin = TimeSpan.Parse(ReadValue(args, ref i, arg));
                    break;
                case "--snapshot-delay-max":
                    snapshotDelayMax = TimeSpan.Parse(ReadValue(args, ref i, arg));
                    break;
                case "--data-file":
                    dataFilePath = ReadValue(args, ref i, arg);
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Unknown option '{arg}'. Use --help for usage.");
                    }

                    bootstrapServers = arg;
                    break;
            }
        }

        if (snapshotCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Snapshot count must be at least 1.");
        }

        var parsed = new SimulationOptions(
            bootstrapServers,
            topic,
            snapshotCount,
            messageDelay,
            snapshotDelayMin,
            snapshotDelayMax,
            dataFilePath,
            showHelp);

        if (parsed.SnapshotDelayMax < parsed.SnapshotDelayMin)
        {
            throw new ArgumentException("--snapshot-delay-max must be greater than or equal to --snapshot-delay-min.");
        }

        return parsed;
    }

    private static string ReadValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Option '{optionName}' requires a value.");
        }

        index++;
        return args[index];
    }
}
