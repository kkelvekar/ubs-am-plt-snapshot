using UBS.AM.PLT.SnapshotWriter.TestProducer;

try
{
    var options = SimulationOptions.Parse(args, Environment.GetEnvironmentVariable);

    if (options.ShowHelp)
    {
        Console.WriteLine(SimulationOptions.HelpText);
        return 0;
    }

    var template = SnapshotTemplateLoader.Load(options.DataFilePath);
    var messages = SnapshotGenerator.GenerateSnapshots(template, options, DateTimeOffset.UtcNow);

    await SnapshotSimulationPublisher.PublishAsync(messages, options, CancellationToken.None);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
