using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Zorb.Compiler.AST;
using Zorb.Compiler.Utils;

namespace Zorb.Compiler.Utils;

public class ErrorReporter
{
    private const string Red = "\u001b[31;1m";
    private const string Cyan = "\u001b[36m";
    private const string Reset = "\u001b[0m";
    private const string White = "\u001b[37;1m";
    private const string Yellow = "\u001b[33;1m";

    private sealed record DiagnosticEntry(string Message, Node? Node, string Severity, string Color);

    private readonly List<DiagnosticEntry> _errors = new();
    private readonly List<DiagnosticEntry> _warnings = new();
    public bool HasErrors => _errors.Count > 0;
    public bool HasWarnings => _warnings.Count > 0;

    public List<string> Errors => _errors.Select(FormatDiagnosticMessage).ToList();
    public List<string> Warnings => _warnings.Select(FormatDiagnosticMessage).ToList();

    public void Error(string message)
    {
        _errors.Add(new DiagnosticEntry(message, null, "error", Red));
    }

    public void Error(string message, int line, int column)
    {
        _errors.Add(new DiagnosticEntry($"{message} at line {line}, column {column}", null, "error", Red));
    }

    public void Error(string message, int line, int column, string file)
    {
        _errors.Add(new DiagnosticEntry($"{file}:{line}:{column}: error: {message}", null, "error", Red));
    }

    public void Error(Node node, string message)
    {
        _errors.Add(new DiagnosticEntry(message, node, "error", Red));
    }

    public void Warning(string message)
    {
        _warnings.Add(new DiagnosticEntry(message, null, "warning", Yellow));
    }

    public void Warning(string message, int line, int column)
    {
        _warnings.Add(new DiagnosticEntry($"{message} at line {line}, column {column}", null, "warning", Yellow));
    }

    public void Warning(string message, int line, int column, string file)
    {
        _warnings.Add(new DiagnosticEntry($"{file}:{line}:{column}: warning: {message}", null, "warning", Yellow));
    }

    public void Warning(Node node, string message)
    {
        _warnings.Add(new DiagnosticEntry(message, node, "warning", Yellow));
    }

    public void ReportAll()
    {
        ReportWarnings();
        ReportErrors();
    }

    public void ReportErrors()
    {
        foreach (var error in _errors)
            RenderDiagnostic(error, Console.Error);
    }

    public void ReportWarnings()
    {
        foreach (var warning in _warnings)
            RenderDiagnostic(warning, Console.Error);
    }

    private static bool HasLocationPrefix(string error)
    {
        var lastColon = error.LastIndexOf(':');
        if (lastColon <= 0)
            return false;

        var prevColon = error.LastIndexOf(':', lastColon - 1);
        if (prevColon <= 0)
            return false;

        if (lastColon <= prevColon + 1)
            return false;

        if (!IsAllDigits(error[(prevColon + 1)..lastColon]))
            return false;

        var severityColon = error.IndexOf(": ", lastColon, StringComparison.Ordinal);
        return severityColon > lastColon;
    }

    private static bool IsAllDigits(string text)
    {
        return text.Length > 0 && text.All(char.IsDigit);
    }

    private static string FormatDiagnosticMessage(DiagnosticEntry diagnostic)
    {
        if (diagnostic.Node != null)
            return $"{diagnostic.Node.File}:{diagnostic.Node.Line}:{diagnostic.Node.Column}: {diagnostic.Severity}: {diagnostic.Message}";

        return diagnostic.Message;
    }

    private static void RenderDiagnostic(DiagnosticEntry diagnostic, TextWriter writer)
    {
        var formattedMessage = FormatDiagnosticMessage(diagnostic);
        if (diagnostic.Node == null)
        {
            writer.WriteLine(HasLocationPrefix(formattedMessage)
                ? formattedMessage
                : $"{char.ToUpperInvariant(diagnostic.Severity[0])}{diagnostic.Severity[1..]}: {formattedMessage}");
            return;
        }

        var node = diagnostic.Node;
        writer.WriteLine($"{White}{node.File}:{node.Line}:{node.Column}: {diagnostic.Color}{diagnostic.Severity}: {White}{diagnostic.Message}{Reset}");
        try
        {
            var lines = File.ReadLines(node.File).ToList();
            if (node.Line > 0 && node.Line <= lines.Count)
            {
                var sourceLine = lines[node.Line - 1];

                writer.WriteLine($"{Cyan}{node.Line,4} | {Reset}{sourceLine}");

                var padding = new string(' ', Math.Max(0, node.Column - 1));
                var underline = new string('^', Math.Max(1, node.Length));

                writer.WriteLine($"{Cyan}     | {diagnostic.Color}{padding}{underline}{Reset}");
            }
        }
        catch (Exception)
        {
            writer.WriteLine($"{diagnostic.Color}Could not load source snippet.{Reset}");
        }

        writer.WriteLine();
    }

    public void ThrowIfErrors()
    {
        if (_errors.Count > 0)
        {
            throw new ZorbCompilerException(string.Join(Environment.NewLine, _errors));
        }
    }
}
