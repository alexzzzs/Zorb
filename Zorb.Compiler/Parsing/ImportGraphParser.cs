using Zorb.Compiler.AST;
using Zorb.Compiler.AST.Statements;
using Zorb.Compiler.Lexer;
using Zorb.Compiler.Parser;
using Zorb.Compiler.Utils;

namespace Zorb.Compiler.Parsing;

public static class ImportGraphParser
{
    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    public static ParseGraphResult ParseWithImports(string entryPath)
    {
        var normalizedEntryPath = NormalizeImportGraphPath(entryPath);
        var visited = new HashSet<string>(PathComparer);
        var files = new Dictionary<string, List<Node>>(PathComparer);
        var errors = new List<string>();
        var entryNodes = ParseRecursive(normalizedEntryPath, visited, files, errors);

        var readOnlyFiles = files.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<Node>)pair.Value.AsReadOnly(),
            PathComparer);

        return new ParseGraphResult(normalizedEntryPath, entryNodes, readOnlyFiles, errors);
    }

    private static List<Node> ParseRecursive(string path, HashSet<string> visited, Dictionary<string, List<Node>> files, List<string> errors)
    {
        path = NormalizeImportGraphPath(path);

        if (!visited.Add(path))
            return files.TryGetValue(path, out var existingNodes) ? existingNodes : new List<Node>();

        if (!File.Exists(path))
            return new List<Node>();

        var source = File.ReadAllText(path);
        List<Token> tokens;

        try
        {
            var lexer = new Lexer.Lexer(source, path);
            tokens = lexer.Tokenize();
        }
        catch (LexerException ex)
        {
            errors.Add($"{ex.File}:{ex.Line}:{ex.Column}: error: {ex.Message}");
            return new List<Node>();
        }

        var parser = new Parser.Parser(tokens, path);
        var nodes = parser.ParseProgram();
        files[path] = nodes;
        errors.AddRange(parser.ErrorReporter.Errors);

        if (parser.ErrorReporter.HasErrors)
            return nodes;

        var currentDir = Path.GetDirectoryName(path) ?? ".";
        foreach (var import in nodes.OfType<ImportNode>())
        {
            if (import.Alias == "c")
                continue;

            var importPath = Path.GetFullPath(Path.IsPathRooted(import.Path)
                ? import.Path
                : Path.Combine(currentDir, import.Path));

            ParseRecursive(importPath, visited, files, errors);
        }

        return nodes;
    }

    private static string NormalizeImportGraphPath(string path)
    {
        return Path.GetFullPath(path);
    }
}

public sealed record ParseGraphResult(
    string EntryPath,
    List<Node> EntryNodes,
    IReadOnlyDictionary<string, IReadOnlyList<Node>> Files,
    List<string> Errors);
