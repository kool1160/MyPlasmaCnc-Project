namespace MyPlasm.Inspector.ProtocolAnalysis;

internal static class OutputPathSafety
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public static void EnsureInputIsNotAnOutput(string inputPath, string outputDirectory)
    {
        try
        {
            string fullInputPath = Normalize(inputPath);
            string resolvedInputPath = ResolveExistingLinks(fullInputPath);

            foreach (string reportFileName in ReportWriter.ReportFileNames)
            {
                string outputPath = Normalize(Path.Combine(outputDirectory, reportFileName));
                if (PathComparer.Equals(fullInputPath, outputPath) ||
                    PathComparer.Equals(resolvedInputPath, ResolveExistingLinks(outputPath)))
                {
                    throw new InputOutputPathCollisionException(reportFileName);
                }
            }
        }
        catch (AnalysisOutputException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            NotSupportedException)
        {
            throw new AnalysisOutputException(
                $"Could not safely validate the input and output paths: {exception.Message}",
                exception);
        }
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string ResolveExistingLinks(string path)
    {
        string root = Path.GetPathRoot(path)
            ?? throw new IOException("A rooted path is required for collision validation.");
        string resolved = root;
        string relative = path[root.Length..];

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
}
