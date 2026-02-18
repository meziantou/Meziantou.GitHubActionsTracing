namespace Meziantou.GitHubActionsTracing;

public readonly record struct GitHubRunIdentifier(string Owner, string Repository, long RunId)
{
    public static bool TryParse(Uri uri, out GitHubRunIdentifier runIdentifier)
    {
        runIdentifier = default;

        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 5 || !segments[2].Equals("actions", StringComparison.OrdinalIgnoreCase) || !segments[3].Equals("runs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!long.TryParse(segments[4], CultureInfo.InvariantCulture, out var runId))
            return false;

        runIdentifier = new GitHubRunIdentifier(segments[0], segments[1], runId);
        return true;
    }

    public static GitHubRunIdentifier Parse(Uri uri)
    {
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The workflow run URL must target github.com", nameof(uri));

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 5 || !segments[2].Equals("actions", StringComparison.OrdinalIgnoreCase) || !segments[3].Equals("runs", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Invalid workflow run URL. Expected format: https://github.com/{owner}/{repo}/actions/runs/{id}", nameof(uri));
        }

        if (!long.TryParse(segments[4], CultureInfo.InvariantCulture, out var runId))
            throw new ArgumentException("Invalid workflow run id in URL", nameof(uri));

        return new GitHubRunIdentifier(segments[0], segments[1], runId);
    }

    public string GetApiPath(string relativePath)
    {
        return $"https://api.github.com/repos/{Owner}/{Repository}{relativePath}";
    }
}
