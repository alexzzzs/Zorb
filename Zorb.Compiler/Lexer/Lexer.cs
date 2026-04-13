using System.Collections.Generic;
using System.Text;
using Zorb.Compiler.Utils;

namespace Zorb.Compiler.Lexer;

    public class Lexer
    {
        private readonly string _source;
        private readonly string _fileName;
        private int _pos = 0;
        private int _line = 1;
        private int _column = 1;

        public Lexer(string source, string fileName = "unknown")
        {
            _source = source;
            _fileName = fileName;
        }

        private (int line, int col) GetPosition(int pos)
        {
            int line = 1, col = 1;
            for (int i = 0; i < pos; i++)
            {
                if (_source[i] == '\n') { line++; col = 1; }
                else { col++; }
            }
            return (line, col);
        }

        private void Advance()
        {
            if (_pos < _source.Length && _source[_pos] == '\n')
            {
                _line++;
                _column = 1;
            }
            else
            {
                _column++;
            }
            _pos++;
        }

        private char PeekChar(int offset)
        {
            var index = _pos + offset;
            if (index >= _source.Length)
                return '\0';
            return _source[index];
        }

        public List<Token> Tokenize()
    {
        var tokens = new List<Token>();

        while (_pos < _source.Length)
        {
            char c = _source[_pos];

            if (char.IsWhiteSpace(c))
            {
                Advance();
                continue;
            }

            var loc = (_line, _column);

            if (c == '{')
            {
                tokens.Add(new Token(TokenType.LBrace, "", loc.Item1, loc.Item2));
                Advance();
                continue;
            }

            if (c == '}')
            {
                tokens.Add(new Token(TokenType.RBrace, "", loc.Item1, loc.Item2));
                Advance();
                continue;
            }

            if (c == '(')
            {
                tokens.Add(new Token(TokenType.LParen, "", loc.Item1, loc.Item2));
                Advance();
                continue;
            }

            if (c == ')')
            {
                tokens.Add(new Token(TokenType.RParen, "", loc.Item1, loc.Item2));
                Advance();
                continue;
            }

            if (c == ',')
            {
                tokens.Add(new Token(TokenType.Comma, "", loc.Item1, loc.Item2));
                Advance();
                continue;
            }

            if (c == ':')
            {
                tokens.Add(new Token(TokenType.Colon, "", loc.Item1, loc.Item2));
                Advance();
                continue;
            }

            if (c == ';')
            {
                tokens.Add(new Token(TokenType.Semicolon, "", loc.Item1, loc.Item2));
                Advance();
                continue;
            }

            if (c == '-' && PeekChar(1) == '>')
            {
                tokens.Add(new Token(TokenType.Arrow, "", loc.Item1, loc.Item2));
                _pos += 2;
                _column += 2;
                continue;
            }

            if (c == '=' && PeekChar(1) == '=')
            {
                tokens.Add(new Token(TokenType.EqualEqual, "", loc.Item1, loc.Item2));
                _pos += 2;
                _column += 2;
                continue;
            }

            if (c == '!' && PeekChar(1) == '=')
            {
                tokens.Add(new Token(TokenType.BangEqual, "", loc.Item1, loc.Item2));
                _pos += 2;
                _column += 2;
                continue;
            }

            if (c == '!')
            {
                tokens.Add(new Token(TokenType.Bang, "", loc.Item1, loc.Item2));
                Advance();
                continue;
            }

            if (c == '>' && PeekChar(1) == '=')
            {
                tokens.Add(new Token(TokenType.GreaterEqual, "", loc.Item1, loc.Item2));
                _pos += 2;
                _column += 2;
                continue;
            }

            if (c == '<' && PeekChar(1) == '=')
            {
                tokens.Add(new Token(TokenType.LessEqual, "", loc.Item1, loc.Item2));
                _pos += 2;
                _column += 2;
                continue;
            }

            if (c == '<' && PeekChar(1) == '<')
            {
                tokens.Add(new Token(TokenType.LShift, "", loc.Item1, loc.Item2));
                _pos += 2;
                _column += 2;
                continue;
            }

            if (c == '>' && PeekChar(1) == '>')
            {
                tokens.Add(new Token(TokenType.RShift, "", loc.Item1, loc.Item2));
                _pos += 2;
                _column += 2;
                continue;
            }

            if (c == '=')
            {
                tokens.Add(new Token(TokenType.Equals, "", loc.Item1, loc.Item2));
                Advance();
                continue;
            }

            if (c == '+')
            {
                tokens.Add(new Token(TokenType.Plus, "", loc.Item1, loc.Item2));
                Advance();
                continue;
            }

            if (c == '-')
            {
                tokens.Add(new Token(TokenType.Minus, "", loc.Item1, loc.Item2));
                Advance();
                continue;
            }

            if (c == '*')
            {
                tokens.Add(new Token(TokenType.Star, "", loc.Item1, loc.Item2));
                Advance();
                continue;
            }

            if (c == '/')
            {
                if (PeekChar(1) == '/')
                {
                    _pos += 2;
                    _column += 2;
                    while (_pos < _source.Length && _source[_pos] != '\n')
                    {
                        _pos++;
                        _column++;
                    }
                    continue;
                }
                tokens.Add(new Token(TokenType.Slash, "", loc.Item1, loc.Item2));
                Advance();
                continue;
            }

            if (c == '%')
            {
                tokens.Add(new Token(TokenType.Percent, "", loc.Item1, loc.Item2));
                Advance();
                continue;
            }

            if (c == '&')
            {
                tokens.Add(new Token(TokenType.Amp, "", loc.Item1, loc.Item2));
                Advance();
                continue;
            }

            if (c == '|')
            {
                tokens.Add(new Token(TokenType.Pipe, "", loc.Item1, loc.Item2));
                Advance();
                continue;
            }

            if (c == '^')
            {
                tokens.Add(new Token(TokenType.Caret, "", loc.Item1, loc.Item2));
                Advance();
                continue;
            }

            if (c == '>') { tokens.Add(new Token(TokenType.Greater, "", loc.Item1, loc.Item2)); Advance(); continue; }
            if (c == '<') { tokens.Add(new Token(TokenType.Less, "", loc.Item1, loc.Item2)); Advance(); continue; }

            if (c == '[')
            {
                tokens.Add(new Token(TokenType.LBracket, "", loc.Item1, loc.Item2));
                Advance();
                continue;
            }

            if (c == ']')
            {
                tokens.Add(new Token(TokenType.RBracket, "", loc.Item1, loc.Item2));
                Advance();
                continue;
            }

            if (c == '.')
            {
                tokens.Add(new Token(TokenType.Dot, "", loc.Item1, loc.Item2));
                Advance();
                continue;
            }

            if (c == '"')
            {
                var startLoc = (_line, _column);
                Advance();
                var value = new StringBuilder();
                while (_pos < _source.Length && _source[_pos] != '"')
                {
                    if (_source[_pos] == '\n')
                        throw new LexerException("Unterminated string literal", startLoc.Item1, startLoc.Item2, _fileName);

                    if (_source[_pos] == '\\')
                    {
                        Advance();
                        if (_pos >= _source.Length)
                            throw new LexerException("Unterminated string literal", startLoc.Item1, startLoc.Item2, _fileName);

                        value.Append(ReadEscapeSequence(startLoc.Item1, startLoc.Item2));
                        continue;
                    }

                    value.Append(_source[_pos]);
                    Advance();
                }

                if (_pos >= _source.Length)
                    throw new LexerException("Unterminated string literal", startLoc.Item1, startLoc.Item2, _fileName);

                Advance();
                tokens.Add(new Token(TokenType.String, value.ToString(), startLoc.Item1, startLoc.Item2));
                continue;
            }

            if (char.IsDigit(c))
            {
                var start = _pos;

                if (c == '0' && (_pos + 1 < _source.Length) && (_source[_pos + 1] == 'x' || _source[_pos + 1] == 'X'))
                {
                    _pos += 2;
                    _column += 2;
                    while (_pos < _source.Length && 
                          (char.IsDigit(_source[_pos]) || 
                          (_source[_pos] >= 'a' && _source[_pos] <= 'f') || 
                          (_source[_pos] >= 'A' && _source[_pos] <= 'F')))
                    {
                        _pos++;
                        _column++;
                    }
                }
                else
                {
                    while (_pos < _source.Length && char.IsDigit(_source[_pos]))
                    {
                        _pos++;
                        _column++;
                    }
                }

                string text = _source.Substring(start, _pos - start);
                tokens.Add(new Token(TokenType.Number, text, loc.Item1, loc.Item2));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = _pos;
                while (_pos < _source.Length && (char.IsLetterOrDigit(_source[_pos]) || _source[_pos] == '_'))
                {
                    _pos++;
                    _column++;
                }

                string text = _source.Substring(start, _pos - start);

                if (text == "fn")
                    tokens.Add(new Token(TokenType.Fn, "", loc.Item1, loc.Item2));
                else if (text == "import")
                    tokens.Add(new Token(TokenType.Import, "", loc.Item1, loc.Item2));
                else if (text == "as")
                    tokens.Add(new Token(TokenType.As, "", loc.Item1, loc.Item2));
                else if (text == "if")
                    tokens.Add(new Token(TokenType.If, "", loc.Item1, loc.Item2));
                else if (text == "while")
                    tokens.Add(new Token(TokenType.While, "", loc.Item1, loc.Item2));
                else if (text == "return")
                    tokens.Add(new Token(TokenType.Return, "", loc.Item1, loc.Item2));
                else if (text == "struct")
                    tokens.Add(new Token(TokenType.Struct, "", loc.Item1, loc.Item2));
                else if (text == "cast")
                    tokens.Add(new Token(TokenType.Cast, "", loc.Item1, loc.Item2));
                else if (text == "extern")
                    tokens.Add(new Token(TokenType.Extern, "", loc.Item1, loc.Item2));
                else if (text == "align")
                    tokens.Add(new Token(TokenType.Align, "", loc.Item1, loc.Item2));
                else if (text == "noinline")
                    tokens.Add(new Token(TokenType.NoInline, "", loc.Item1, loc.Item2));
                else if (text == "noclone")
                    tokens.Add(new Token(TokenType.NoClone, "", loc.Item1, loc.Item2));
                else if (text == "catch")
                    tokens.Add(new Token(TokenType.Catch, "", loc.Item1, loc.Item2));
                else if (text == "const")
                    tokens.Add(new Token(TokenType.Const, "", loc.Item1, loc.Item2));
                else if (text == "error")
                    tokens.Add(new Token(TokenType.Error, "", loc.Item1, loc.Item2));
                else if (text == "export")
                    tokens.Add(new Token(TokenType.Export, "", loc.Item1, loc.Item2));
                else if (text == "else")
                    tokens.Add(new Token(TokenType.Else, "", loc.Item1, loc.Item2));
                else if (text == "continue")
                    tokens.Add(new Token(TokenType.Continue, "", loc.Item1, loc.Item2));
                else if (text == "break")
                    tokens.Add(new Token(TokenType.Break, "", loc.Item1, loc.Item2));
                else if (text == "true")
                    tokens.Add(new Token(TokenType.True, "", loc.Item1, loc.Item2));
                else if (text == "false")
                    tokens.Add(new Token(TokenType.False, "", loc.Item1, loc.Item2));
                else if (text.Equals("builtin", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenType.Builtin, "", loc.Item1, loc.Item2));
                else
                    tokens.Add(new Token(TokenType.Identifier, text, loc.Item1, loc.Item2));

                continue;
            }

            Advance();
        }

        tokens.Add(new Token(TokenType.Eof, "", _line, _column));
        return tokens;
    }

    private char ReadEscapeSequence(int line, int column)
    {
        var escaped = _source[_pos];
        Advance();

        return escaped switch
        {
            '"' => '"',
            '\\' => '\\',
            'n' => '\n',
            'r' => '\r',
            't' => '\t',
            '0' => '\0',
            _ => throw new LexerException($"Unsupported escape sequence '\\{escaped}'", line, column, _fileName)
        };
    }
}
