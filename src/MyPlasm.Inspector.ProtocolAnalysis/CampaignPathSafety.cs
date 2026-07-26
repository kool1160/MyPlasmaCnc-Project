namespace MyPlasm.Inspector.ProtocolAnalysis;

internal sealed record CampaignPathSet(
    IReadOnlyList<string> AnalysisDirectories,
    string OutputDirectory);

internal static class CampaignPathSafety
{
    private static readonly StringComparer PathComparer =
        StringComparer.OrdinalIgnoreCase;

    public static CampaignPathSet Validate(
        IReadOnlyList<string> analysisDirectories,
        string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(analysisDirectories);
        if (analysisDirectories.Count != 3)
        {
            throw new ArgumentException(
                "Exactly three explicit --analysis directories are required.",
                nameof(analysisDirectories));
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException(
                "An explicit comparison output directory is required.",
                nameof(outputDirectory));
        }

        try
        {
            List<(string Full, string Resolved)> inputs = [];
            for (int index = 0; index < analysisDirectories.Count; index++)
            {
                string candidate = analysisDirectories[index];
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    throw new ArgumentException(
                        $"Analysis directory {index + 1} is empty.",
                        nameof(analysisDirectories));
                }

                string full = Normalize(candidate);
                if (!Directory.Exists(full))
                {
                    throw new CampaignInputValidationException(
                        $"Analysis directory {index + 1} does not exist.");
                }

                string resolved = ResolveExistingLinks(full);
                if (inputs.Any(input =>
                        PathComparer.Equals(input.Full, full) ||
                        PathComparer.Equals(input.Resolved, resolved)))
                {
                    throw new CampaignInputValidationException(
                        "Duplicate analysis directories are not permitted, including normalized or link-resolved aliases.");
                }

                inputs.Add((full, resolved));
            }

            string fullOutput = Normalize(outputDirectory);
            string resolvedOutput = ResolveExistingLinks(fullOutput);
            foreach ((string fullInput, string resolvedInput) in inputs)
            {
                if (PathsOverlap(fullInput, fullOutput) ||
                    PathsOverlap(resolvedInput, resolvedOutput))
                {
                    throw new CampaignPathCollisionException(
                        "The comparison output directory overlaps an input analysis directory. " +
                        "Choose a separate directory; --overwrite never permits modifying input evidence.");
                }
            }

            return new CampaignPathSet(
                inputs.Select(input => input.Full).ToArray(),
                fullOutput);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                NotSupportedException)
        {
            throw new AnalysisOutputException(
                $"Could not safely validate campaign paths: {exception.Message}",
                exception);
        }
    }

    internal static string ResolveExistingLinks(string path)
    {
        string normalized = Normalize(path);
        string root = Path.GetPathRoot(normalized)
            ?? throw new IOException(
                "A rooted path is required for campaign path validation.");
        string resolved = root;
        string relative = normalized[root.Length..];

        foreach (string segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(resolved, segment);
            FileSystemInfo? info = Directory.Exists(candidate)
                ? new DirectoryInfo(candidate)
                : File.Exists(candidate)
                    ? new FileInfo(candidate)
                    : null;
            FileSystemInfo? target = info?.ResolveLinkTarget(returnFinalTarget: true);
            resolved = target is null ? candidate : target.FullName;
        }

        return Normalize(resolved);
    }

    internal static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal static bool PathsOverlap(string first, string second) =>
        IsSameOrDescendant(first, second) ||
        IsSameOrDescendant(second, first);

    private static bool IsSameOrDescendant(string candidate, string parent)
    {
        if (PathComparer.Equals(candidate, parent))
        {
            return true;
        }

        string prefix = parent + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
