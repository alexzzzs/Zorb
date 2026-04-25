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

    private readonly List<string> _errors = new();
    private readonly List<string> _warnings = new();
    public bool HasErrors => _errors.Count > 0;
    public bool HasWarnings => _warnings.Count > 0;

    public List<string> Errors => _errors;
    public List<string> Warnings => _warnings;

    public void Error(string message)
    {
        _errors.Add(message);
    }

    public void Error(string message, int line, int column)
    {
        _errors.Add($"{message} at line {line}, column {column}");
    }

    public void Error(string message, int line, int column, string file)
    {
        _errors.Add($"{file}:{line}:{column}: error: {message}");
    }

    public void Error(Node node, string message)
    {
        ReportError(node, message);
    }

    public void Warning(string message)
    {
        _warnings.Add(message);
    }

    public void Warning(string message, int line, int column)
    {
        _warnings.Add($"{message} at line {line}, column {column}");
    }

    public void Warning(string message, int line, int column, string file)
    {
        _warnings.Add($"{file}:{line}:{column}: warning: {message}");
    }

    public void Warning(Node node, string message)
    {
        ReportWarning(node, message);
    }

    public void ReportError(Node node, string message)
    {
        ReportDiagnostic(_errors, node, message, "error", Red);
    }

    public void ReportWarning(Node node, string message)
    {
        ReportDiagnostic(_warnings, node, message, "warning", "\u001b[33;1m");
    }

    public void ReportAll()
    {
        ReportWarnings();
        ReportErrors();
    }

    public void ReportErrors()
    {
        foreach (var error in _errors)
        {
            Console.Error.WriteLine(HasLocationPrefix(error) ? error : $"Error: {error}");
        }
    }

    public void ReportWarnings()
    {
        foreach (var warning in _warnings)
        {
            Console.Error.WriteLine(HasLocationPrefix(warning) ? warning : $"Warning: {warning}");
        }
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

    private void ReportDiagnostic(List<string> sink, Node node, string message, string severity, string color)
    {
        sink.Add($"{node.File}:{node.Line}:{node.Column}: {severity}: {message}");

        Console.WriteLine($"{White}{node.File}:{node.Line}:{node.Column}: {color}{severity}: {White}{message}{Reset}");

        try
        {
            var lines = File.ReadLines(node.File).ToList();
            if (node.Line > 0 && node.Line <= lines.Count)
            {
                string sourceLine = lines[node.Line - 1];

                Console.WriteLine($"{Cyan}{node.Line,4} | {Reset}{sourceLine}");

                string padding = new string(' ', Math.Max(0, node.Column - 1));
                string underline = new string('^', Math.Max(1, node.Length));

                Console.WriteLine($"{Cyan}     | {color}{padding}{underline}{Reset}");
            }
        }
        catch (Exception)
        {
            Console.WriteLine($"{color}Could not load source snippet.{Reset}");
        }
        Console.WriteLine();
    }

    public void ThrowIfErrors()
    {
        if (_errors.Count > 0)
        {
            throw new ZorbCompilerException(string.Join(Environment.NewLine, _errors));
        }
    }
}
