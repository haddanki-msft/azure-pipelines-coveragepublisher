namespace PtrPublisher;

internal sealed class Options
{
    public const string TokenEnvironmentVariable = "ADO_ACCESS_TOKEN";

    public static readonly string[] SupportedRunners = { "VSTest", "JUnit", "NUnit", "XUnit", "CTest" };

    public Uri? CollectionUrl { get; private set; }
    public string? Project { get; private set; }
    public string TestRunner { get; private set; } = "VSTest";
    public string ResultsDirectory { get; private set; } = ".";
    public string Pattern { get; private set; } = "*.trx";
    public List<string> Files { get; } = new();
    public string? RunTitle { get; private set; }
    public string? Platform { get; private set; }
    public string? Configuration { get; private set; }
    public string TestRunSystem { get; private set; } = "GitHubActions";
    public bool MergeResults { get; private set; } = true;
    public bool PublishRunAttachments { get; private set; } = true;
    public bool FailOnTestFailure { get; private set; }
    public bool DryRun { get; private set; }
    public int TimeoutSeconds { get; private set; } = 600;
    public bool ShowHelp { get; private set; }

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            string Next()
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new UsageException($"Missing value for {name}.");
                }
                return args[++i];
            }

            switch (name)
            {
                case "--collection-url": options.CollectionUrl = ParseCollectionUrl(Next()); break;
                case "--project": options.Project = Next(); break;
                case "--test-runner": options.TestRunner = ParseRunner(Next()); break;
                case "--results-dir": options.ResultsDirectory = Next(); break;
                case "--pattern": options.Pattern = Next(); break;
                case "--file": options.Files.Add(Next()); break;
                case "--run-title": options.RunTitle = Next(); break;
                case "--platform": options.Platform = Next(); break;
                case "--configuration": options.Configuration = Next(); break;
                case "--test-run-system": options.TestRunSystem = Next(); break;
                case "--merge-results": options.MergeResults = ParseBool(name, Next()); break;
                case "--publish-run-attachments": options.PublishRunAttachments = ParseBool(name, Next()); break;
                case "--fail-on-test-failure": options.FailOnTestFailure = ParseBool(name, Next()); break;
                case "--timeout-seconds": options.TimeoutSeconds = ParseTimeout(Next()); break;
                case "--dry-run": options.DryRun = true; break;
                case "-h":
                case "--help": options.ShowHelp = true; break;
                default: throw new UsageException($"Unknown argument '{name}'.");
            }
        }

        if (!options.ShowHelp && !options.DryRun)
        {
            if (options.CollectionUrl is null) throw new UsageException("--collection-url is required.");
            if (string.IsNullOrWhiteSpace(options.Project)) throw new UsageException("--project is required.");
        }
        return options;
    }

    public const string Usage = """
        Usage: dotnet PtrPublisher.dll --collection-url <url> --project <name> [options]

        Publishes test result files to Azure DevOps Test Runs using the Ta PublishTestResults library.
        The access token is read from the ADO_ACCESS_TOKEN environment variable (never from arguments).

          --collection-url <url>            https://dev.azure.com/<org>
          --project <name>                  Azure DevOps project
          --test-runner <name>              VSTest (default) | JUnit | NUnit | XUnit | CTest
          --results-dir <dir>               Directory searched recursively (default: .)
          --pattern <glob>                  File name pattern (default: *.trx)
          --file <path>                     Explicit result file; repeatable, overrides --results-dir/--pattern
          --run-title <title>               Test run title
          --platform <value>                Build platform
          --configuration <value>           Build configuration
          --test-run-system <value>         Source tag stored on the run (max 16 chars; default: "GitHubActions")
          --merge-results <bool>            Merge all files into one run (default: true)
          --publish-run-attachments <bool>  Upload result files as run attachments (default: true)
          --fail-on-test-failure <bool>     Exit 2 when published runs contain failures (default: false)
          --timeout-seconds <n>             Overall timeout, 30-3600 (default: 600)
          --dry-run                         Parse only, do not contact Azure DevOps

        Exit codes: 0 success, 1 usage/parse/publish error, 2 failed tests (with --fail-on-test-failure true).
        """;

    private static Uri ParseCollectionUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new UsageException("--collection-url must be an absolute https URL.");
        }
        return uri;
    }

    private static string ParseRunner(string value)
    {
        var match = SupportedRunners.FirstOrDefault(r => r.Equals(value, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new UsageException($"--test-runner must be one of: {string.Join(", ", SupportedRunners)}.");
    }

    private static bool ParseBool(string name, string value) =>
        bool.TryParse(value, out var result) ? result : throw new UsageException($"{name} must be true or false.");

    private static int ParseTimeout(string value) =>
        int.TryParse(value, out var seconds) && seconds is >= 30 and <= 3600
            ? seconds
            : throw new UsageException("--timeout-seconds must be between 30 and 3600.");
}

internal sealed class UsageException : Exception
{
    public UsageException(string message) : base(message) { }
}
