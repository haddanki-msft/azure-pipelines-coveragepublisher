using System.Diagnostics;
using System.Text.Json;
using Microsoft.TeamFoundation.TestClient.PublishTestResults;
using Microsoft.TeamFoundation.TestManagement.WebApi;
using Microsoft.VisualStudio.Services.OAuth;
using Microsoft.VisualStudio.Services.WebApi;

namespace PtrPublisher;

internal static class Program
{
    private const int ExitSuccess = 0;
    private const int ExitError = 1;
    private const int ExitTestsFailed = 2;

    private static async Task<int> Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine(Options.Usage);
            return ExitError;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(Options.Usage);
            return ExitSuccess;
        }

        var token = Environment.GetEnvironmentVariable(Options.TokenEnvironmentVariable);
        if (!options.DryRun && string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine($"error: {Options.TokenEnvironmentVariable} is not set.");
            return ExitError;
        }

        var trace = new PrefixedTraceListener(token);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        try
        {
            return await RunAsync(options, token, trace, cts.Token);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitError;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            trace.WriteLine($"Timed out after {options.TimeoutSeconds} seconds.");
            return ExitError;
        }
        catch (Exception ex)
        {
            trace.WriteLine($"Unhandled error: {ex}");
            return ExitError;
        }
    }

    private static async Task<int> RunAsync(Options options, string? token, PrefixedTraceListener trace, CancellationToken cancellationToken)
    {
        var files = ResolveFiles(options);
        if (files.Count == 0)
        {
            trace.WriteLine("No test result files matched; nothing to publish.");
            return ExitError;
        }
        trace.WriteLine($"Found {files.Count} {options.TestRunner} result file(s).");

        // A GitHub run has no ADO build: buildId 0 and no build/release URIs keep the run build-less.
        var runName = options.RunTitle ?? DefaultRunName(options.TestRunner);
        var context = new TestRunContext(
            owner: null,
            platform: options.Platform,
            configuration: options.Configuration,
            buildId: 0,
            buildUri: null,
            releaseUri: null,
            releaseEnvironmentUri: null,
            runName: runName,
            testRunSystem: options.TestRunSystem);

        var parser = CreateParser(options.TestRunner, trace);
        var testRunData = parser.ParseTestResultFiles(context, files)?.GetTestRunData();
        if (testRunData is null || testRunData.Count == 0)
        {
            trace.WriteLine("The parser returned no test run data. Check that --test-runner matches the file format.");
            return ExitError;
        }

        var parsedResults = testRunData.Sum(r => r.TestResults?.Count ?? 0);
        trace.WriteLine($"Parsed {testRunData.Count} run(s) with {parsedResults} result(s).");

        if (options.DryRun)
        {
            WriteSummary(new Summary(runName, true, parsedResults, Array.Empty<RunSummary>(), false));
            return ExitSuccess;
        }

        var connection = new VssConnection(options.CollectionUrl!, new VssOAuthAccessTokenCredential(token!));
        using var publisher = new TestRunPublisher(connection, trace);
        var publishOptions = new PublishOptions
        {
            IsMergeTestResultsToSingleRun = options.MergeResults,
            IsAddTestRunAttachments = options.PublishRunAttachments,
        };

        var sw = Stopwatch.StartNew();
        var runs = await publisher.PublishTestRunDataAsync(context, options.Project!, testRunData, publishOptions, cancellationToken);
        if (runs is null || runs.Count == 0)
        {
            trace.WriteLine("Publishing returned no test runs.");
            return ExitError;
        }
        trace.WriteLine($"Published {runs.Count} run(s) in {sw.Elapsed.TotalSeconds:F1}s.");

        // Runs returned by the publisher can carry pre-completion counts, so re-read them from the server.
        var client = connection.GetClient<TestManagementHttpClient>();
        var summaries = new List<RunSummary>();
        foreach (var run in runs)
        {
            var current = await client.GetTestRunByIdAsync(options.Project!, run.Id, cancellationToken: cancellationToken);
            summaries.Add(new RunSummary(
                current.Id,
                current.State,
                current.TotalTests,
                current.PassedTests,
                current.NotApplicableTests,
                current.UnanalyzedTests,
                $"{options.CollectionUrl!.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(options.Project!)}/_testManagement/runs?runId={current.Id}&_a=runCharts"));
        }

        var hasFailures = summaries.Any(s => s.Total != s.Passed + s.NotApplicable);
        WriteSummary(new Summary(runName, false, parsedResults, summaries, hasFailures));
        WriteGitHubOutputs(summaries, hasFailures);

        if (hasFailures && options.FailOnTestFailure)
        {
            trace.WriteLine("Published runs contain failed tests.");
            return ExitTestsFailed;
        }
        return ExitSuccess;
    }

    private static List<string> ResolveFiles(Options options)
    {
        if (options.Files.Count > 0)
        {
            var missing = options.Files.Where(f => !File.Exists(f)).ToList();
            if (missing.Count > 0)
            {
                throw new UsageException($"File(s) not found: {string.Join(", ", missing)}");
            }
            return options.Files.Select(Path.GetFullPath).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();
        }

        if (!Directory.Exists(options.ResultsDirectory))
        {
            return new List<string>();
        }
        return Directory.EnumerateFiles(options.ResultsDirectory, options.Pattern, SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    // Mirrors TestResultsPublisher.exe's parser selection; feature-flagged options use the library defaults.
    private static ITestResultParser CreateParser(string runner, TraceListener trace) => runner switch
    {
        "VSTest" => new TrxResultParser(trace),
        "JUnit" => new JUnitResultParser(trace),
        "NUnit" => new NUnitResultParser(trace),
        "XUnit" => new XUnitResultParser(trace),
        "CTest" => new CTestResultParser(trace),
        _ => throw new UsageException($"Unsupported test runner '{runner}'."),
    };

    private static string DefaultRunName(string runner)
    {
        var repo = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
        var runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID");
        var attempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT");
        return string.IsNullOrEmpty(runId)
            ? $"{runner}_TestResults"
            : $"GitHub {repo} run {runId} attempt {attempt}";
    }

    private static void WriteSummary(Summary summary) =>
        Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));

    private static void WriteGitHubOutputs(IReadOnlyList<RunSummary> runs, bool hasFailures)
    {
        var outputPath = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
        if (string.IsNullOrEmpty(outputPath))
        {
            return;
        }
        File.AppendAllLines(outputPath, new[]
        {
            $"run-ids={string.Join(',', runs.Select(r => r.RunId))}",
            $"run-url={runs[0].Url}",
            $"has-failures={hasFailures.ToString().ToLowerInvariant()}",
        });
    }

    private sealed record Summary(string RunName, bool DryRun, int ParsedResults, IReadOnlyList<RunSummary> Runs, bool HasFailures);

    private sealed record RunSummary(int RunId, string State, int Total, int Passed, int NotApplicable, int Unanalyzed, string Url);
}
