namespace Meziantou.GitHubActionsTracing;

internal sealed class TraceLoadOptions
{
    public bool IncludeBinlog { get; init; } = true;

    public bool IncludeTests { get; init; } = true;

    public TimeSpan MinimumTestDuration { get; init; } = TimeSpan.Zero;

    public TimeSpan MinimumBinlogDuration { get; init; } = TimeSpan.Zero;
}
