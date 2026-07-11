using System.Text.Json;

namespace UBS.AM.PLT.SnapshotWriter.TestProducer;

public static class SnapshotTemplateLoader
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static SnapshotTemplate Load(string path)
    {
        using var stream = File.OpenRead(path);
        var template = JsonSerializer.Deserialize<SnapshotTemplate>(stream, Options)
            ?? throw new InvalidOperationException($"Simulation data file '{path}' was empty.");

        Validate(template, path);
        return template;
    }

    private static void Validate(SnapshotTemplate template, string path)
    {
        if (template.AccountIds.Count == 0)
        {
            throw new InvalidOperationException($"Simulation data file '{path}' must contain at least one accountId.");
        }

        if (template.Stages.Count == 0)
        {
            throw new InvalidOperationException($"Simulation data file '{path}' must contain at least one stage.");
        }

        var payloadTypes = template.Payloads.Select(payload => payload.PayloadType).ToArray();
        var expected = new[] { "header", "instruments", "calculations", "settings" };
        if (!payloadTypes.SequenceEqual(expected))
        {
            throw new InvalidOperationException(
                $"Simulation payloads must be ordered as: {string.Join(", ", expected)}.");
        }
    }
}
