using System.Text.Json;

internal static partial class Program
{
    private const string FrontendParityManifestFileName = "frontend-parity.json";
    private const int FrontendParityManifestVersion = 2;

    internal sealed record FixtureParityCase(
        string Name,
        string InputPath,
        string Expected,
        string Feature);

    private sealed record FixtureParityEntry(
        string Name,
        string Path,
        string Classification,
        string Feature,
        string Expected,
        string? Gate,
        string Reason);

    private sealed record FixtureParityManifest(
        int Version,
        IReadOnlyList<FixtureParityEntry> Entries);

    private static void RunFixtureParityClassificationTests()
    {
        var projectRoot = GetProjectRoot();
        var manifest = LoadFixtureParityManifest(projectRoot);
        if (manifest.Version != FrontendParityManifestVersion)
            throw new Exception($"Unsupported frontend parity manifest version {manifest.Version}.");

        var sourceEntries = EnumerateParitySources(projectRoot).ToArray();
        var sourcePaths = sourceEntries
            .Select(sourcePath => NormalizeManifestPath(Path.GetRelativePath(projectRoot, sourcePath)))
            .ToHashSet(StringComparer.Ordinal);
        var explicitEntries = manifest.Entries
            .ToDictionary(entry => NormalizeManifestPath(entry.Path), StringComparer.Ordinal);
        if (explicitEntries.Count != manifest.Entries.Count)
            throw new Exception("frontend parity manifest contains duplicate paths.");

        foreach (var explicitPath in explicitEntries.Keys)
        {
            if (!sourcePaths.Contains(explicitPath))
                throw new Exception($"frontend parity manifest classifies an input outside the parity corpus: '{explicitPath}'.");
        }

        var entryNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Entries)
        {
            if (!entryNames.Add(entry.Name))
                throw new Exception($"frontend parity manifest contains duplicate name '{entry.Name}'.");
            ValidateParityEntry(projectRoot, entry);
        }

        foreach (var sourcePath in sourceEntries)
        {
            var relativePath = NormalizeManifestPath(Path.GetRelativePath(projectRoot, sourcePath));
            if (!explicitEntries.ContainsKey(relativePath))
                throw new Exception($"frontend parity manifest does not explicitly classify '{relativePath}'.");
        }

        var gatedCases = LoadFrontendParityCases(projectRoot);
        if (gatedCases.Count == 0)
            throw new Exception("frontend parity manifest has no enabled frontend cases.");
        foreach (var parityCase in gatedCases)
        {
            if (!File.Exists(parityCase.InputPath))
                throw new Exception($"Enabled frontend parity input does not exist: '{parityCase.InputPath}'.");
        }
    }

    internal static IReadOnlyList<FixtureParityCase> LoadFrontendParityCases(string projectRoot)
    {
        var manifest = LoadFixtureParityManifest(projectRoot);
        var explicitCases = manifest.Entries
            .Where(entry => string.Equals(entry.Gate, "frontend", StringComparison.Ordinal))
            .Select(entry => new FixtureParityCase(
                entry.Name,
                Path.GetFullPath(Path.Combine(projectRoot, entry.Path)),
                entry.Expected,
                entry.Feature));
        return explicitCases
            .OrderBy(parityCase => parityCase.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static FixtureParityManifest LoadFixtureParityManifest(string projectRoot)
    {
        var manifestPath = Path.Combine(projectRoot, "tests/csharp", FrontendParityManifestFileName);
        if (!File.Exists(manifestPath))
            throw new Exception($"Missing frontend parity manifest: '{manifestPath}'.");

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var manifest = JsonSerializer.Deserialize<FixtureParityManifest>(File.ReadAllText(manifestPath), options);
        if (manifest == null)
            throw new Exception("frontend parity manifest is empty.");
        return manifest;
    }

    private static IEnumerable<string> EnumerateParitySources(string projectRoot)
    {
        var fixtureRoot = Path.Combine(projectRoot, "tests/csharp", "fixtures");
        foreach (var mainPath in Directory.EnumerateFiles(fixtureRoot, "main.zorb", SearchOption.AllDirectories))
            yield return Path.GetFullPath(mainPath);

        var nativeFixtureRoot = Path.Combine(projectRoot, "compiler", "self-check", "fixtures");
        foreach (var fixturePath in Directory.EnumerateFiles(nativeFixtureRoot, "*.zorb", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(nativeFixtureRoot, fixturePath);
            var isRootFixture = !relativePath.Contains(Path.DirectorySeparatorChar) &&
                                !relativePath.Contains(Path.AltDirectorySeparatorChar);
            if (isRootFixture || string.Equals(Path.GetFileName(fixturePath), "main.zorb", StringComparison.Ordinal))
                yield return Path.GetFullPath(fixturePath);
        }

        var examplesRoot = Path.Combine(projectRoot, "examples");
        foreach (var examplePath in Directory.EnumerateFiles(examplesRoot, "*.zorb", SearchOption.AllDirectories))
        {
            var directory = Path.GetDirectoryName(examplePath);
            var isMain = string.Equals(Path.GetFileName(examplePath), "main.zorb", StringComparison.Ordinal);
            if (isMain || string.IsNullOrEmpty(directory) || !File.Exists(Path.Combine(directory, "main.zorb")))
                yield return Path.GetFullPath(examplePath);
        }
    }

    private static void ValidateParityEntry(string projectRoot, FixtureParityEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.Path) ||
            string.IsNullOrWhiteSpace(entry.Classification) || string.IsNullOrWhiteSpace(entry.Feature) ||
            string.IsNullOrWhiteSpace(entry.Expected) || string.IsNullOrWhiteSpace(entry.Reason))
        {
            throw new Exception("frontend parity manifest contains an incomplete entry.");
        }

        if (!new[] { "deferred", "native-verified", "differential" }.Contains(entry.Classification, StringComparer.Ordinal))
            throw new Exception($"frontend parity entry '{entry.Name}' has unknown classification '{entry.Classification}'.");
        if (!new[] { "success", "lexical-failure", "parse-failure", "import-failure", "semantic-failure" }.Contains(entry.Expected, StringComparer.Ordinal))
            throw new Exception($"frontend parity entry '{entry.Name}' has unknown expected outcome '{entry.Expected}'.");
        if (entry.Gate != null && !string.Equals(entry.Gate, "frontend", StringComparison.Ordinal))
            throw new Exception($"frontend parity entry '{entry.Name}' has unknown gate '{entry.Gate}'.");
        if (string.Equals(entry.Gate, "frontend", StringComparison.Ordinal) &&
            !string.Equals(entry.Classification, "differential", StringComparison.Ordinal))
        {
            throw new Exception($"Enabled frontend parity entry '{entry.Name}' must be classified as differential.");
        }

        var inputPath = Path.GetFullPath(Path.Combine(projectRoot, entry.Path));
        if (!File.Exists(inputPath))
            throw new Exception($"frontend parity entry '{entry.Name}' references missing input '{entry.Path}'.");
    }

    private static string NormalizeManifestPath(string path) => path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
}
