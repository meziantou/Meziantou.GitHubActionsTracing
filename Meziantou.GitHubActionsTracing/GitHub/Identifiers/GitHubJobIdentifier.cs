namespace Meziantou.GitHubActionsTracing;

internal readonly record struct GitHubJobIdentifier(string Owner, string Repository, long RunId, long JobId)
{
    public static GitHubJobIdentifier Parse(Uri uri)
    {
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The workflow job URL must target github.com", nameof(uri));

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 7 ||
            !segments[2].Equals("actions", StringComparison.OrdinalIgnoreCase) ||
            !segments[3].Equals("runs", StringComparison.OrdinalIgnoreCase) ||
            !segments[5].Equals("job", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Invalid workflow job URL. Expected format: https://github.com/{owner}/{repo}/actions/runs/{run-id}/job/{job-id}", nameof(uri));
        }

        if (!long.TryParse(segments[4], CultureInfo.InvariantCulture, out var runId))
            throw new ArgumentException("Invalid workflow run id in URL", nameof(uri));

        if (!long.TryParse(segments[6], CultureInfo.InvariantCulture, out var jobId))
            throw new ArgumentException("Invalid workflow job id in URL", nameof(uri));

        return new GitHubJobIdentifier(segments[0], segments[1], runId, jobId);
    }
}
