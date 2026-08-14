using System.Text.Json;

namespace UBS.AM.PLT.Snapshot.Worker.LiveTesting;

internal sealed class SnapshotTemplateLoader(IHostEnvironment environment)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static readonly string[] ExpectedPayloadTypes = ["header", "orders", "calculations", "settings"];
    private readonly string dataDirectory = Path.Combine(environment.ContentRootPath, "LiveTesting", "Data");

    public SnapshotTemplate Load(string templateFileName)
    {
        if (string.IsNullOrWhiteSpace(templateFileName)
            || !string.Equals(Path.GetFileName(templateFileName), templateFileName, StringComparison.Ordinal)
            || !string.Equals(Path.GetExtension(templateFileName), ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new SnapshotSimulationValidationException(
                "TemplateFileName must be a JSON filename without a directory path.");
        }

        var path = Path.Combine(dataDirectory, templateFileName);

        try
        {
            using var stream = File.OpenRead(path);
            var template = JsonSerializer.Deserialize<SnapshotTemplate>(stream, Options)
                ?? throw new SnapshotSimulationValidationException(
                    $"Simulation template '{templateFileName}' was empty.");

            Validate(template, templateFileName);
            return template;
        }
        catch (SnapshotSimulationValidationException)
        {
            throw;
        }
        catch (IOException ex)
        {
            throw new SnapshotSimulationValidationException(
                $"Simulation template '{templateFileName}' was not found.", ex);
        }
        catch (JsonException ex)
        {
            throw new SnapshotSimulationValidationException(
                $"Simulation template '{templateFileName}' is not valid JSON.", ex);
        }
    }

    private static void Validate(SnapshotTemplate template, string templateFileName)
    {
        if (template.AccountIds.Count == 0)
        {
            throw new SnapshotSimulationValidationException(
                $"Simulation template '{templateFileName}' must contain at least one accountId.");
        }

        var payloadTypes = template.Payloads.Select(payload => payload.PayloadType).ToArray();
        if (!payloadTypes.SequenceEqual(ExpectedPayloadTypes))
        {
            throw new SnapshotSimulationValidationException(
                $"Simulation payloads must be ordered as: {string.Join(", ", ExpectedPayloadTypes)}.");
        }
    }
}
