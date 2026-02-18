using System.Globalization;
using System.Text.RegularExpressions;

namespace Meziantou.GitHubActionsTracing;

public sealed partial class GitHubRepositoryFilter
{
    // User provided regex patterns can be very expensive to execute, so we set a timeout to prevent DoS attacks.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private readonly HashSet<string> _allowedRepositoriesExact;
    private readonly Regex[] _allowedRepositoriesPatterns;

    public GitHubRepositoryFilter(IEnumerable<string>? allowedRepositoriesExact, IEnumerable<string>? allowedRepositoriesPatterns)
    {
        _allowedRepositoriesExact = CreateAllowedRepositoriesSet(allowedRepositoriesExact);
        _allowedRepositoriesPatterns = CreateAllowedRepositoriesPatterns(allowedRepositoriesPatterns);
    }

    public bool IsEnabled => _allowedRepositoriesExact.Count > 0 || _allowedRepositoriesPatterns.Length > 0;

    public bool IsAllowed(GitHubRunIdentifier runIdentifier)
    {
        return IsAllowed(runIdentifier.Owner, runIdentifier.Repository);
    }

    public bool IsAllowed(string owner, string repository)
    {
        var repositoryFullName = $"{owner}/{repository}";
        return IsAllowed(repositoryFullName);
    }

    public bool IsAllowed(string repositoryFullName)
    {
        if (string.IsNullOrWhiteSpace(repositoryFullName))
        {
            return false;
        }

        if (!IsEnabled)
        {
            return true;
        }

        if (_allowedRepositoriesExact.Contains(repositoryFullName))
        {
            return true;
        }

        foreach (var pattern in _allowedRepositoriesPatterns)
        {
            if (pattern.IsMatch(repositoryFullName))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> CreateAllowedRepositoriesSet(IEnumerable<string>? repositories)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (repositories is null)
        {
            return result;
        }

        foreach (var repository in repositories)
        {
            if (string.IsNullOrWhiteSpace(repository))
            {
                continue;
            }

            var repositoryFullName = repository.Trim();
            if (!IsValidRepositoryFullName(repositoryFullName))
            {
                continue;
            }

            result.Add(repositoryFullName);
        }

        return result;
    }

    private static Regex[] CreateAllowedRepositoriesPatterns(IEnumerable<string>? patterns)
    {
        if (patterns is null)
        {
            return [];
        }

        var result = new List<Regex>();
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            try
            {
                result.Add(new Regex(pattern.Trim(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout));
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"Invalid repository pattern '{pattern}'"), nameof(patterns), ex);
            }
        }

        return [.. result];
    }

    private static bool IsValidRepositoryFullName(string repositoryFullName)
    {
        return RepositoryFullNameRegex.IsMatch(repositoryFullName);
    }

    [GeneratedRegex("^[^/\\s]+/[^/\\s]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: -1)]
    private static partial Regex RepositoryFullNameRegex { get; }
}