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

        // Maps an identifier to its contextual keyword TokenType when in attribute context.
        // Returns the mapped TokenType or null if the identifier is not a contextual keyword.
        public static TokenType? MapContextualKeyword(string identifier)
        {
            return identifier switch
            {
                "abi" => TokenType.Abi,
                "section" => TokenType.Section,
                "packed" => TokenType.Packed,
                "layout" => TokenType.Layout,
                "offset" => TokenType.Offset,
                "noinline" => TokenType.NoInline,
                "noclone" => TokenType.NoClone,
                "volatile" => TokenType.Volatile,
                _ => null
            };
        }

        public List<Token> Tokenize()
        {
            var tokens = new List<Token>();

            while (_pos < _source.Length)
            {
                var c = _source[_pos];
                if (char.IsWhiteSpace(c))
                {
                    Advance();
                    continue;
                }

                var loc = (_line, _column);
                if (TryTokenizeOperatorOrPunctuation(tokens, c, loc))
                    continue;

                if (c == '"')
                {
                    TokenizeStringLiteral(tokens, loc);
                    continue;
                }

                if (char.IsDigit(c))
                {
                    TokenizeNumber(tokens, c, loc);
                    continue;
                }

                if (char.IsLetter(c) || c == '_')
                {
                    TokenizeIdentifierOrKeyword(tokens, loc);
                    continue;
                }

                Advance();
            }

            tokens.Add(new Token(TokenType.Eof, "", _line, _column));
            return tokens;
        }

        private bool TryTokenizeOperatorOrPunctuation(List<Token> tokens, char c, (int line, int col) loc)
        {
            if (TryTokenizeTwoCharacterOperator(tokens, c, loc))
                return true;

            if (c == '/')
            {
                if (PeekChar(1) == '/')
                {
                    SkipLineComment();
                    return true;
                }

                AddToken(tokens, TokenType.Slash, loc);
                Advance();
                return true;
            }

            return c switch
            {
                '{' => AddSingleCharacterToken(tokens, TokenType.LBrace, loc),
                '}' => AddSingleCharacterToken(tokens, TokenType.RBrace, loc),
                '(' => AddSingleCharacterToken(tokens, TokenType.LParen, loc),
                ')' => AddSingleCharacterToken(tokens, TokenType.RParen, loc),
                ',' => AddSingleCharacterToken(tokens, TokenType.Comma, loc),
                ':' => AddSingleCharacterToken(tokens, TokenType.Colon, loc),
                ';' => AddSingleCharacterToken(tokens, TokenType.Semicolon, loc),
                '!' => AddSingleCharacterToken(tokens, TokenType.Bang, loc),
                '=' => AddSingleCharacterToken(tokens, TokenType.Equals, loc),
                '+' => AddSingleCharacterToken(tokens, TokenType.Plus, loc),
                '-' => AddSingleCharacterToken(tokens, TokenType.Minus, loc),
                '*' => AddSingleCharacterToken(tokens, TokenType.Star, loc),
                '%' => AddSingleCharacterToken(tokens, TokenType.Percent, loc),
                '&' => AddSingleCharacterToken(tokens, TokenType.Amp, loc),
                '|' => AddSingleCharacterToken(tokens, TokenType.Pipe, loc),
                '^' => AddSingleCharacterToken(tokens, TokenType.Caret, loc),
                '>' => AddSingleCharacterToken(tokens, TokenType.Greater, loc),
                '<' => AddSingleCharacterToken(tokens, TokenType.Less, loc),
                '[' => AddSingleCharacterToken(tokens, TokenType.LBracket, loc),
                ']' => AddSingleCharacterToken(tokens, TokenType.RBracket, loc),
                '.' => AddSingleCharacterToken(tokens, TokenType.Dot, loc),
                _ => false
            };
        }

        private bool TryTokenizeTwoCharacterOperator(List<Token> tokens, char c, (int line, int col) loc)
        {
            var tokenType = MatchTwoCharacterOperator(c);
            return tokenType.HasValue && AddTwoCharacterToken(tokens, tokenType.Value, loc);
        }

        private TokenType? MatchTwoCharacterOperator(char current)
        {
            var next = PeekChar(1);
            return (current, next) switch
            {
                ('-', '>') => TokenType.Arrow,
                ('=', '=') => TokenType.EqualEqual,
                ('!', '=') => TokenType.BangEqual,
                ('>', '=') => TokenType.GreaterEqual,
                ('<', '=') => TokenType.LessEqual,
                ('<', '<') => TokenType.LShift,
                ('>', '>') => TokenType.RShift,
                ('&', '&') => TokenType.AndAnd,
                ('|', '|') => TokenType.OrOr,
                _ => null
            };
        }

        private bool AddSingleCharacterToken(List<Token> tokens, TokenType tokenType, (int line, int col) loc)
        {
            AddToken(tokens, tokenType, loc);
            Advance();
            return true;
        }

        private bool AddTwoCharacterToken(List<Token> tokens, TokenType tokenType, (int line, int col) loc)
        {
            AddToken(tokens, tokenType, loc);
            _pos += 2;
            _column += 2;
            return true;
        }

        private static void AddToken(List<Token> tokens, TokenType tokenType, (int line, int col) loc, string text = "")
        {
            tokens.Add(new Token(tokenType, text, loc.line, loc.col));
        }

        private void SkipLineComment()
        {
            _pos += 2;
            _column += 2;
            while (_pos < _source.Length && _source[_pos] != '\n')
            {
                _pos++;
                _column++;
            }
        }

        private void TokenizeStringLiteral(List<Token> tokens, (int line, int col) startLoc)
        {
            Advance();
            var value = new StringBuilder();
            while (_pos < _source.Length && _source[_pos] != '"')
            {
                AppendStringLiteralCharacter(value, startLoc);
            }

            EnsureStringLiteralTerminated(startLoc);

            Advance();
            AddToken(tokens, TokenType.String, startLoc, value.ToString());
        }

        private void AppendStringLiteralCharacter(StringBuilder value, (int line, int col) startLoc)
        {
            if (_source[_pos] == '\n')
                throw new LexerException("Unterminated string literal", startLoc.line, startLoc.col, _fileName);

            if (_source[_pos] == '\\')
            {
                AppendEscapedStringCharacter(value, startLoc);
                return;
            }

            value.Append(_source[_pos]);
            Advance();
        }

        private void AppendEscapedStringCharacter(StringBuilder value, (int line, int col) startLoc)
        {
            Advance();
            EnsureStringLiteralTerminated(startLoc);
            value.Append(ReadEscapeSequence(startLoc.line, startLoc.col));
        }

        private void EnsureStringLiteralTerminated((int line, int col) startLoc)
        {
            if (_pos >= _source.Length)
                throw new LexerException("Unterminated string literal", startLoc.line, startLoc.col, _fileName);
        }

        private void TokenizeNumber(List<Token> tokens, char c, (int line, int col) loc)
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

            var text = _source.Substring(start, _pos - start);
            AddToken(tokens, TokenType.Number, loc, text);
        }

        private void TokenizeIdentifierOrKeyword(List<Token> tokens, (int line, int col) loc)
        {
            var start = _pos;
            while (_pos < _source.Length && (char.IsLetterOrDigit(_source[_pos]) || _source[_pos] == '_'))
            {
                _pos++;
                _column++;
            }

            var text = _source.Substring(start, _pos - start);
            var keywordToken = GetKeywordTokenType(text);
            if (keywordToken.HasValue)
            {
                AddToken(tokens, keywordToken.Value, loc);
                return;
            }

            if (text.Equals("builtin", StringComparison.OrdinalIgnoreCase))
            {
                AddToken(tokens, TokenType.Builtin, loc);
                return;
            }

            AddToken(tokens, TokenType.Identifier, loc, text);
        }

        private static TokenType? GetKeywordTokenType(string text)
        {
            return text switch
            {
                "fn" => TokenType.Fn,
                "import" => TokenType.Import,
                "as" => TokenType.As,
                "if" => TokenType.If,
                "while" => TokenType.While,
                "for" => TokenType.For,
                "switch" => TokenType.Switch,
                "match" => TokenType.Match,
                "case" => TokenType.Case,
                "return" => TokenType.Return,
                "struct" => TokenType.Struct,
                "enum" => TokenType.Enum,
                "union" => TokenType.Union,
                "cast" => TokenType.Cast,
                "extern" => TokenType.Extern,
                "align" => TokenType.Align,
                "catch" => TokenType.Catch,
                "const" => TokenType.Const,
                "error" => TokenType.Error,
                "export" => TokenType.Export,
                "else" => TokenType.Else,
                "continue" => TokenType.Continue,
                "break" => TokenType.Break,
                "true" => TokenType.True,
                "false" => TokenType.False,
                _ => null
            };
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
