using System.Net;
using System.Text;
using System.Text.Json;
using Reqnroll;

namespace UBS.AM.PLT.Snapshot.IntegrationTests.Support;

internal static class CiTestReportWriter
{
    private static readonly object Sync = new();
    private static readonly List<ScenarioResult> Results = [];

    private static bool IsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("GITLAB_CI"), "true", StringComparison.OrdinalIgnoreCase);

    public static void Record(FeatureContext featureContext, ScenarioContext scenarioContext)
    {
        if (!IsEnabled)
        {
            return;
        }

        var executionStatus = scenarioContext.ScenarioExecutionStatus;
        var outcome = scenarioContext.TestError is not null
            ? "Failed"
            : executionStatus switch
            {
                ScenarioExecutionStatus.OK => "Passed",
                ScenarioExecutionStatus.Skipped => "Skipped",
                _ => "Failed"
            };

        var result = new ScenarioResult(
            featureContext.FeatureInfo.Title,
            scenarioContext.ScenarioInfo.Title,
            scenarioContext.ScenarioInfo.CombinedTags,
            outcome,
            executionStatus.ToString(),
            scenarioContext.TestError?.ToString());

        lock (Sync)
        {
            Results.Add(result);
        }
    }

    public static void WriteReports()
    {
        if (!IsEnabled)
        {
            return;
        }

        ScenarioResult[] results;
        lock (Sync)
        {
            results = [.. Results];
        }

        var repositoryRoot = Environment.GetEnvironmentVariable("CI_PROJECT_DIR") ?? Directory.GetCurrentDirectory();
        var reportDirectory = Path.Combine(repositoryRoot, "test-reports");
        Directory.CreateDirectory(reportDirectory);

        var generatedAtUtc = DateTimeOffset.UtcNow;
        var passed = results.Count(result => result.Outcome == "Passed");
        var failed = results.Count(result => result.Outcome == "Failed");
        var skipped = results.Count(result => result.Outcome == "Skipped");
        var report = new
        {
            generatedAtUtc,
            summary = new { total = results.Length, passed, failed, skipped },
            features = results
                .GroupBy(result => result.Feature)
                .OrderBy(group => group.Key)
                .Select(group => new
                {
                    name = group.Key,
                    scenarios = group.OrderBy(result => result.Scenario)
                })
        };

        File.WriteAllText(
            Path.Combine(reportDirectory, "test-report-latest.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(
            Path.Combine(reportDirectory, "test-report-latest.html"),
            BuildHtml(results, generatedAtUtc, passed, failed, skipped));
    }

    private static string BuildHtml(
        IReadOnlyCollection<ScenarioResult> results,
        DateTimeOffset generatedAtUtc,
        int passed,
        int failed,
        int skipped)
    {
        var html = new StringBuilder();
        html.Append("""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Snapshot Writer Regression Test Report</title>
              <style>
                :root { color-scheme: light dark; font-family: Inter, Segoe UI, sans-serif; }
                body { margin: 0; background: #f3f5f8; color: #172033; }
                main { max-width: 1100px; margin: 0 auto; padding: 32px 20px 64px; }
                h1 { margin-bottom: 4px; }
                .generated { color: #667085; margin-top: 0; }
                .summary { display: grid; grid-template-columns: repeat(4, minmax(120px, 1fr)); gap: 12px; margin: 24px 0; }
                .card, .feature { background: white; border: 1px solid #dfe3ea; border-radius: 10px; box-shadow: 0 2px 8px #1018280d; }
                .card { padding: 16px; }
                .card strong { display: block; font-size: 28px; }
                .feature { margin-top: 16px; overflow: hidden; }
                .feature h2 { margin: 0; padding: 16px 18px; background: #eef2f7; font-size: 18px; }
                table { width: 100%; border-collapse: collapse; }
                th, td { padding: 12px 18px; border-top: 1px solid #e7eaf0; text-align: left; vertical-align: top; }
                .status { font-weight: 700; }
                .Passed { color: #087443; }
                .Failed { color: #c1152f; }
                .Skipped { color: #9a6700; }
                .tags { color: #667085; font-size: 12px; }
                pre { white-space: pre-wrap; overflow-wrap: anywhere; background: #fff1f3; padding: 12px; border-radius: 6px; }
                @media (max-width: 650px) { .summary { grid-template-columns: repeat(2, 1fr); } th:nth-child(2), td:nth-child(2) { display: none; } }
              </style>
            </head>
            <body><main>
              <h1>Snapshot Writer Regression Test Report</h1>
            """);
        html.Append("<p class=\"generated\">Generated ")
            .Append(WebUtility.HtmlEncode(generatedAtUtc.ToString("u")))
            .AppendLine("</p>");
        html.Append("<section class=\"summary\">")
            .Append(SummaryCard("Total", results.Count, string.Empty))
            .Append(SummaryCard("Passed", passed, "Passed"))
            .Append(SummaryCard("Failed", failed, "Failed"))
            .Append(SummaryCard("Skipped", skipped, "Skipped"))
            .AppendLine("</section>");

        foreach (var feature in results.GroupBy(result => result.Feature).OrderBy(group => group.Key))
        {
            html.Append("<section class=\"feature\"><h2>")
                .Append(WebUtility.HtmlEncode(feature.Key))
                .AppendLine("</h2><table><thead><tr><th>Scenario</th><th>Execution status</th><th>Outcome</th></tr></thead><tbody>");

            foreach (var scenario in feature.OrderBy(result => result.Scenario))
            {
                html.Append("<tr><td><strong>")
                    .Append(WebUtility.HtmlEncode(scenario.Scenario))
                    .Append("</strong>");
                if (scenario.Tags.Length > 0)
                {
                    html.Append("<div class=\"tags\">")
                        .Append(WebUtility.HtmlEncode(string.Join(" ", scenario.Tags.Select(tag => $"@{tag}"))))
                        .Append("</div>");
                }

                html.Append("</td><td>")
                    .Append(WebUtility.HtmlEncode(scenario.ExecutionStatus))
                    .Append("</td><td class=\"status ")
                    .Append(scenario.Outcome)
                    .Append("\">")
                    .Append(scenario.Outcome);
                if (scenario.Error is not null)
                {
                    html.Append("<pre>").Append(WebUtility.HtmlEncode(scenario.Error)).Append("</pre>");
                }

                html.AppendLine("</td></tr>");
            }

            html.AppendLine("</tbody></table></section>");
        }

        html.AppendLine("</main></body></html>");
        return html.ToString();
    }

    private static string SummaryCard(string label, int value, string cssClass) =>
        $"<div class=\"card {cssClass}\"><span>{label}</span><strong>{value}</strong></div>";

    private sealed record ScenarioResult(
        string Feature,
        string Scenario,
        string[] Tags,
        string Outcome,
        string ExecutionStatus,
        string? Error);
}
