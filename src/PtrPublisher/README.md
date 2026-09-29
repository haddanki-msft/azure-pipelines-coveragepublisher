# PtrPublisher (POC)

A small net10 console app that publishes test result files to Azure DevOps Test Runs from any
OS by calling the Ta `Microsoft.TeamFoundation.PublishTestResults` NuGet package directly. It
replaces the Windows-only `TestResultsPublisher.exe` (net472) used by `tools/ptr`.

The NuGet package contains only the library (parsers and `TestRunPublisher`). The EXE's
orchestration (environment-variable input, parser selection, publish, failure check) is not in
the package, so this app re-implements that thin layer.

## Usage

```
dotnet publish src/PtrPublisher -c Release -o out
ADO_ACCESS_TOKEN=<entra token for 499b84ac-1321-427f-aa17-267ca6975798> \
dotnet out/PtrPublisher.dll --collection-url https://dev.azure.com/<org> --project <project> \
  --results-dir TestResults --pattern "*.trx"
```

Run `dotnet out/PtrPublisher.dll --help` for all options. Use `--dry-run` to parse without
contacting Azure DevOps. The token is only read from `ADO_ACCESS_TOKEN`, never from arguments.

| Exit code | Meaning |
|---|---|
| 0 | Published (or parsed, with `--dry-run`) |
| 1 | Usage, parse, authentication or publish error |
| 2 | Published, runs contain failures and `--fail-on-test-failure true` |

The run is build-less (`buildId` 0) and tagged with `TestRunSystem = "GitHub Actions - PTR"`.
On GitHub the app writes `run-ids`, `run-url` and `has-failures` to `GITHUB_OUTPUT`.

## POC findings

- `20.279.0-preview` cannot be restored from nuget.org: it pins
  `Microsoft.VisualStudio.Services.BlobStore.Client [20.279.37722-buildid32102961]`, which is not
  published there. The app uses `20.278.1-preview`, the latest restorable version.
- Local publish on Windows/net10 created a completed, build-less run with results and the TRX
  attachment. The LogStore (blob) attachment upload returned `Bad Request` and the library fell
  back to the FileService upload, which succeeded. Check whether this also happens on GitHub
  runners.
- Feature-flagged parser options used by the EXE (for example hierarchical xUnit parsing and
  custom test fields) use the library defaults here.

The `ptr-nuget-console.yml` workflow builds and dry-runs the app on Windows, Linux and macOS for
pull requests, and publishes to the POC project on the trusted branch.
