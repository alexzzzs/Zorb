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
        _warnings.Add($"{node.File}:{node.Line}:{node.Column}: warning: {message}");
    }

    public void ReportError(Node node, string message)
    {
        _errors.Add($"{node.File}:{node.Line}:{node.Column}: error: {message}");

        Console.WriteLine($"{White}{node.File}:{node.Line}:{node.Column}: {Red}error: {White}{message}{Reset}");

        try
        {
            var lines = File.ReadLines(node.File).ToList();
            if (node.Line > 0 && node.Line <= lines.Count)
            {
                string sourceLine = lines[node.Line - 1];

                Console.WriteLine($"{Cyan}{node.Line,4} | {Reset}{sourceLine}");

                string padding = new string(' ', node.Column - 1);
                string underline = new string('^', Math.Max(1, node.Length));

                Console.WriteLine($"{Cyan}     | {Red}{padding}{underline}{Reset}");
            }
        }
        catch (Exception)
        {
            Console.WriteLine($"{Red}Could not load source snippet.{Reset}");
        }
        Console.WriteLine();
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
        var firstColon = error.IndexOf(':');
        if (firstColon <= 0)
            return false;

        var secondColon = error.IndexOf(':', firstColon + 1);
        if (secondColon <= firstColon + 1)
            return false;

        return secondColon + 1 < error.Length && char.IsDigit(error[firstColon + 1]);
    }

    public void ThrowIfErrors()
    {
        if (_errors.Count > 0)
        {
            throw new ZorbCompilerException(string.Join(Environment.NewLine, _errors));
        }
    }
}
