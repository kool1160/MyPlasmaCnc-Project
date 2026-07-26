using System.Reflection;

namespace MyPlasm.Inspector.App;

internal static class ApplicationBuildEvidence
{
    internal const string UnknownSourceCommit = "Unknown";

    public static string SourceCommit
    {
        get
        {
            try
            {
                string? value = Assembly.GetEntryAssembly()?
                    .GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(attribute =>
                        string.Equals(
                            attribute.Key,
                            "MyPlasmSourceCommit",
                            StringComparison.Ordinal))
                    ?.Value;
                return value is { Length: 40 } &&
                    value.All(character => Uri.IsHexDigit(character))
                        ? value.ToLowerInvariant()
                        : UnknownSourceCommit;
            }
            catch
            {
                return UnknownSourceCommit;
            }
        }
    }
}
