using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Diagnostics;
using Meziantou.Framework;

namespace Meziantou.GitHubActionsTracing;

internal static class GitHubRunDownloader
{
    private const int PageSize = 100;
    private static readonly HttpClient GitHubHttpClient = CreateGitHubHttpClient();
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<FullPath> DownloadAsync(Uri workflowRunUrl, FullPath temporaryDirectory, CancellationToken cancellationToken)
    {
        var runIdentifier = GitHubRunIdentifier.Parse(workflowRunUrl);

        var metadataDirectory = temporaryDirectory / "metadata";
        var logsDirectory = temporaryDirectory / "logs" / "jobs";
        var artifactsDirectory = temporaryDirectory / "artifacts";

        Directory.CreateDirectory(metadataDirectory);
        Directory.CreateDirectory(logsDirectory);
        Directory.CreateDirectory(artifactsDirectory);

        var run = await GetJsonAsync<WorkflowRunResponse>(GitHubHttpClient, runIdentifier.GetApiPath($"/actions/runs/{runIdentifier.RunId}"), cancellationToken);
        await WriteJsonAsync(metadataDirectory / "run.json", run, cancellationToken);

        AppLog.Info($"Run: {run.Id} / {run.Name ?? "(no name)"}");

        var jobs = await GetAllJobsAsync(GitHubHttpClient, runIdentifier, cancellationToken);
        await WriteJsonAsync(metadataDirectory / "jobs.json", new WorkflowJobsResponse { Jobs = jobs }, cancellationToken);
        AppLog.Info($"Jobs: {jobs.Count}");

        var artifacts = await GetAllArtifactsAsync(GitHubHttpClient, runIdentifier, cancellationToken);
        await WriteJsonAsync(metadataDirectory / "artifacts.json", new WorkflowArtifactsResponse { Artifacts = artifacts }, cancellationToken);
        AppLog.Info($"Artifacts: {artifacts.Count}");

        foreach (var job in jobs)
        {
            AppLog.Info($"Downloading logs for job {job.Id} ({job.Name})");
            var content = await GetBytesAsync(GitHubHttpClient, runIdentifier.GetApiPath($"/actions/jobs/{job.Id}/logs"), cancellationToken);
            var logText = DecodeLogContent(content);
            await File.WriteAllTextAsync(logsDirectory / $"{job.Id}.log", logText, cancellationToken);
            await WriteJsonAsync(metadataDirectory / $"job-{job.Id}.json", job, cancellationToken);
        }

        await DownloadArtifactsAsync(GitHubHttpClient, runIdentifier, artifactsDirectory, artifacts, cancellationToken);

        return temporaryDirectory;
    }

    public static async Task<FullPath> DownloadJobAsync(Uri jobUrl, FullPath outputDirectory, CancellationToken cancellationToken)
    {
        var jobIdentifier = GitHubJobIdentifier.Parse(jobUrl);
        var runIdentifier = new GitHubRunIdentifier(jobIdentifier.Owner, jobIdentifier.Repository, jobIdentifier.RunId);

        Directory.CreateDirectory(outputDirectory);
        var metadataDirectory = outputDirectory / "metadata";
        var logsDirectory = outputDirectory / "logs" / "jobs";
        var artifactsDirectory = outputDirectory / "artifacts";

        Directory.CreateDirectory(metadataDirectory);
        Directory.CreateDirectory(logsDirectory);
        Directory.CreateDirectory(artifactsDirectory);

        var run = await GetJsonAsync<WorkflowRunResponse>(GitHubHttpClient, runIdentifier.GetApiPath($"/actions/runs/{runIdentifier.RunId}"), cancellationToken);
        await WriteJsonAsync(metadataDirectory / "run.json", run, cancellationToken);
        AppLog.Info($"Run: {run.Id} / {run.Name ?? "(no name)"}");

        var job = await GetJsonAsync<WorkflowRunJob>(GitHubHttpClient, runIdentifier.GetApiPath($"/actions/jobs/{jobIdentifier.JobId}"), cancellationToken);
        await WriteJsonAsync(metadataDirectory / "jobs.json", new WorkflowJobsResponse { Jobs = [job] }, cancellationToken);
        await WriteJsonAsync(metadataDirectory / $"job-{job.Id}.json", job, cancellationToken);
        AppLog.Info($"Job: {job.Id} / {job.Name ?? "(no name)"}");

        AppLog.Info($"Downloading logs for job {job.Id} ({job.Name})");
        var content = await GetBytesAsync(GitHubHttpClient, runIdentifier.GetApiPath($"/actions/jobs/{job.Id}/logs"), cancellationToken);
        var logText = DecodeLogContent(content);
        await File.WriteAllTextAsync(logsDirectory / $"{job.Id}.log", logText, cancellationToken);

        var artifacts = await GetAllArtifactsAsync(GitHubHttpClient, runIdentifier, cancellationToken);
        await WriteJsonAsync(metadataDirectory / "artifacts.json", new WorkflowArtifactsResponse { Artifacts = artifacts }, cancellationToken);
        AppLog.Info($"Artifacts: {artifacts.Count}");
        await DownloadArtifactsAsync(GitHubHttpClient, runIdentifier, artifactsDirectory, artifacts, cancellationToken);

        return outputDirectory;
    }

    private static async Task DownloadArtifactsAsync(HttpClient httpClient, GitHubRunIdentifier runIdentifier, FullPath artifactsDirectory, IReadOnlyCollection<WorkflowRunArtifact> artifacts, CancellationToken cancellationToken)
    {
        foreach (var artifact in artifacts)
        {
            var artifactDirectory = artifactsDirectory / $"{artifact.Id}-{SanitizeFileName(artifact.Name)}";
            Directory.CreateDirectory(artifactDirectory);

            AppLog.Info($"Downloading artifact {artifact.Id} ({artifact.Name})");
            var artifactZipPath = artifactDirectory / "artifact.zip";
            var bytes = await GetBytesAsync(httpClient, runIdentifier.GetApiPath($"/actions/artifacts/{artifact.Id}/zip"), cancellationToken);
            await File.WriteAllBytesAsync(artifactZipPath, bytes, cancellationToken);

            var extractedDirectory = artifactDirectory / "files";
            extractedDirectory.CreateParentDirectory();
            ZipFile.ExtractToDirectory(artifactZipPath, extractedDirectory, overwriteFiles: true);

            await WriteJsonAsync(artifactDirectory / "artifact.json", artifact, cancellationToken);

            var extractedFileCount = Directory.EnumerateFiles(extractedDirectory, "*", SearchOption.AllDirectories).Count();
            AppLog.Info($"Extracted artifact {artifact.Id}: {extractedFileCount} file(s)");
        }
    }

    private static HttpClient CreateGitHubHttpClient()
    {
        var httpClient = SharedHttpClient.CreateHttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Meziantou.GitHubActionsTracing/1.0");
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        var token = GetGitHubToken();
        if (!string.IsNullOrWhiteSpace(token))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return httpClient;
    }

    private static string? GetGitHubToken()
    {
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            token = Environment.GetEnvironmentVariable("GH_TOKEN");
        }

        if (!string.IsNullOrWhiteSpace(token))
            return token;

        try
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = "auth token",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(processStartInfo);
            if (process is null)
                return null;

            if (!process.WaitForExit(milliseconds: 5000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            if (process.ExitCode is not 0)
                return null;

            token = process.StandardOutput.ReadToEnd().Trim();
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch
        {
        }

        return null;
    }

    private static async Task<List<WorkflowRunJob>> GetAllJobsAsync(HttpClient httpClient, GitHubRunIdentifier runIdentifier, CancellationToken cancellationToken)
    {
        var jobs = new List<WorkflowRunJob>();

        for (var page = 1; ; page++)
        {
            var response = await GetJsonAsync<WorkflowJobsResponse>(httpClient, runIdentifier.GetApiPath($"/actions/runs/{runIdentifier.RunId}/jobs?per_page={PageSize}&page={page}"), cancellationToken);

            var currentPageJobs = response.Jobs ?? [];
            jobs.AddRange(currentPageJobs);

            if (currentPageJobs.Count < PageSize)
                break;
        }

        return jobs;
    }

    private static async Task<List<WorkflowRunArtifact>> GetAllArtifactsAsync(HttpClient httpClient, GitHubRunIdentifier runIdentifier, CancellationToken cancellationToken)
    {
        var artifacts = new List<WorkflowRunArtifact>();

        for (var page = 1; ; page++)
        {
            var response = await GetJsonAsync<WorkflowArtifactsResponse>(
                httpClient,
                runIdentifier.GetApiPath($"/actions/runs/{runIdentifier.RunId}/artifacts?per_page={PageSize}&page={page}"), cancellationToken);

            var currentPageArtifacts = response.Artifacts ?? [];
            artifacts.AddRange(currentPageArtifacts);

            if (currentPageArtifacts.Count < PageSize)
                break;
        }

        return artifacts;
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient httpClient, string relativeOrAbsoluteUrl, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(relativeOrAbsoluteUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"GitHub API call failed ({response.StatusCode}) for '{relativeOrAbsoluteUrl}': {Trim(body, 500)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync<T>(stream, JsonSerializerOptions, cancellationToken);
        return result ?? throw new InvalidOperationException($"Unable to deserialize GitHub response for '{relativeOrAbsoluteUrl}'");
    }

    private static async Task<byte[]> GetBytesAsync(HttpClient httpClient, string relativeOrAbsoluteUrl, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(relativeOrAbsoluteUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"GitHub API call failed ({response.StatusCode}) for '{relativeOrAbsoluteUrl}': {Trim(body, 500)}");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static async Task WriteJsonAsync<T>(FullPath path, T value, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, JsonSerializerOptions, cancellationToken);
    }

    private static string DecodeLogContent(byte[] bytes)
    {
        if (IsZip(bytes))
        {
            using var memoryStream = new MemoryStream(bytes);
            using var zipArchive = new ZipArchive(memoryStream, ZipArchiveMode.Read);
            var entry = zipArchive.Entries.FirstOrDefault();
            if (entry is null)
                return string.Empty;

            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        if (IsGZip(bytes))
        {
            using var memoryStream = new MemoryStream(bytes);
            using var gzipStream = new GZipStream(memoryStream, CompressionMode.Decompress);
            using var reader = new StreamReader(gzipStream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static bool IsZip(byte[] bytes) => bytes.Length >= 2 && bytes[0] is 0x50 && bytes[1] is 0x4B;

    private static bool IsGZip(byte[] bytes) => bytes.Length >= 2 && bytes[0] is 0x1F && bytes[1] is 0x8B;

    private static string SanitizeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "artifact";

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = string.Concat(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch));
        return string.IsNullOrWhiteSpace(sanitized) ? "artifact" : sanitized;
    }

    private static string Trim(string value, int maxLength)
    {
        if (value.Length <= maxLength)
            return value;

        return value[..maxLength] + "...";
    }
}
