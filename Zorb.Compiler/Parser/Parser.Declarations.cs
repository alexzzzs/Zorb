using System.Collections.Generic;
using System.Linq;
using Zorb.Compiler.AST;
using Zorb.Compiler.AST.Expressions;
using Zorb.Compiler.AST.Statements;
using Zorb.Compiler.Lexer;
using Zorb.Compiler.Utils;

namespace Zorb.Compiler.Parser;

public partial class Parser
{
    private Node ParseTopLevelDeclaration(bool isExported)
    {
        if (Current.Type == TokenType.Import)
        {
            ErrorReporter.Error("Cannot export an import declaration.", Current.Line, Current.Column, _fileName);
            return ParseImport();
        }

        if (Current.Type == TokenType.Error)
        {
            var decl = ParseErrorDecl();
            decl.IsExported = true;
            return decl;
        }

        if (Current.Type == TokenType.Const)
        {
            var decl = (VariableDeclarationNode)ParseVarDecl(true);
            decl.IsExported = true;
            return decl;
        }

        if (Current.Type == TokenType.Struct)
        {
            var decl = ParseStruct();
            decl.IsExported = true;
            return decl;
        }

        if (Current.Type == TokenType.Enum)
        {
            var decl = ParseEnum();
            decl.IsExported = true;
            return decl;
        }

        if (Current.Type == TokenType.Union)
        {
            var decl = ParseUnion();
            decl.IsExported = true;
            return decl;
        }

        if (Current.Type == TokenType.Fn || Current.Type == TokenType.Extern)
        {
            var decl = ParseFunction();
            decl.IsExported = true;
            return decl;
        }

        if (Current.Type == TokenType.LBracket)
        {
            var tokenAfterAttributes = PeekDeclarationAfterAttributeLists();
            if (tokenAfterAttributes == TokenType.Fn || tokenAfterAttributes == TokenType.Extern)
            {
                var decl = ParseFunction();
                decl.IsExported = true;
                return decl;
            }
            if (tokenAfterAttributes == TokenType.Struct)
            {
                var decl = ParseStruct();
                decl.IsExported = true;
                return decl;
            }

            var attributes = ParseAttributes();
            var varDecl = (VariableDeclarationNode)ParseVarDecl();
            ApplyVariableAttributes(varDecl, attributes);
            varDecl.IsExported = true;
            return varDecl;
        }

        if (Current.Type == TokenType.Identifier && Peek(1).Type == TokenType.Colon)
        {
            var decl = (VariableDeclarationNode)ParseVarDecl();
            decl.IsExported = true;
            return decl;
        }

        ErrorReporter.Error(
            $"Expected an exportable top-level declaration after 'export'. Use 'export fn', 'export struct', 'export enum', 'export union', 'export const', or 'export name: Type = ...'. Got {DescribeToken(Current)}.",
            Current.Line,
            Current.Column,
            _fileName);
        Advance();
        return new VariableDeclarationNode
        {
            Name = "__invalid_export",
            TypeName = new TypeNode { Name = "i32" },
            IsConst = true,
            IsExported = true
        };
    }

    private ImportNode ParseImport()
    {
        var startToken = Expect(TokenType.Import);

        if (Current.Type == TokenType.Identifier && Current.Value == "c")
        {
            Advance();
            var headerToken = Expect(TokenType.String, "Expected C header string after 'import c'.");
            var import = new ImportNode { Path = headerToken.Value, Alias = "c" };
            StampNode(import, startToken);
            return import;
        }

        var pathToken = Expect(TokenType.String, "Expected import path string after 'import'.");
        string? alias = null;

        if (Current.Type == TokenType.As)
        {
            Advance();
            alias = Expect(TokenType.Identifier, "Expected alias name after 'as' in import.").Value;
        }

        var node = new ImportNode { Path = pathToken.Value, Alias = alias };
        StampNode(node, startToken);
        return node;
    }

    private TokenType PeekDeclarationAfterAttributeLists()
    {
        var lookahead = 0;
        while (Peek(lookahead).Type == TokenType.LBracket)
        {
            var bracketDepth = 0;
            while (_pos + lookahead < _tokens.Count)
            {
                var tokenType = Peek(lookahead).Type;
                if (tokenType == TokenType.LBracket)
                    bracketDepth++;
                else if (tokenType == TokenType.RBracket)
                    bracketDepth--;

                lookahead++;
                if (bracketDepth == 0)
                    break;
            }
        }

        return Peek(lookahead).Type;
    }

    private StructNode ParseStruct()
    {
        var attributes = ParseStructAttributes();
        var startToken = Expect(TokenType.Struct);

        var (path, name) = ParseDottedDeclName(
            "Expected struct name after 'struct'.",
            "Expected identifier after '.' in struct name.");

        Expect(TokenType.LBrace, "Expected '{' to start struct body.");

        var fields = new List<StructField>();
        while (Current.Type != TokenType.RBrace && Current.Type != TokenType.Eof)
        {
            var fieldAttributes = ParseFieldAttributes();
            var fieldNameToken = Expect(TokenType.Identifier, "Expected struct field name.");
            var fieldName = fieldNameToken.Value;
            Expect(TokenType.Colon, $"Expected ':' after field name '{fieldName}'.");
            var fieldType = ParseType();
            var field = new StructField
            {
                Name = fieldName,
                TypeName = fieldType,
                Attributes = fieldAttributes.Attributes,
                OffsetExpr = fieldAttributes.OffsetExpr
            };
            StampNode(field, fieldNameToken);
            fields.Add(field);

            if (Current.Type == TokenType.Comma)
            {
                Advance();
                continue;
            }
        }

        Expect(TokenType.RBrace, "Expected '}' to close struct body.");

        var node = new StructNode { NamespacePath = path, Name = name, Attributes = attributes.Attributes, AlignExpr = attributes.AlignExpr, Fields = fields };
        StampNode(node, startToken);
        return node;
    }

    private EnumNode ParseEnum()
    {
        var startToken = Expect(TokenType.Enum);

        var (path, name) = ParseDottedDeclName(
            "Expected enum name after 'enum'.",
            "Expected identifier after '.' in enum name.");

        Expect(TokenType.Colon, "Expected ':' after enum name.");
        var underlyingType = ParseType();
        Expect(TokenType.LBrace, "Expected '{' to start enum body.");

        var members = new List<EnumMember>();
        while (Current.Type != TokenType.RBrace && Current.Type != TokenType.Eof)
        {
            var memberNameToken = Expect(TokenType.Identifier, "Expected enum member name.");
            Expr? value = null;
            if (Match(TokenType.Equals))
                value = ParseExpression();

            var member = new EnumMember
            {
                Name = memberNameToken.Value,
                Value = value
            };
            StampNode(member, memberNameToken);
            members.Add(member);

            if (Current.Type == TokenType.Comma)
            {
                Advance();
                continue;
            }

            if (Current.Type == TokenType.RBrace)
                break;

            ErrorReporter.Error(
                $"Expected ',' or '}}' after enum member '{member.Name}', got {DescribeToken(Current)}.",
                Current.Line,
                Current.Column,
                _fileName);
            Advance();
        }

        Expect(TokenType.RBrace, "Expected '}' to close enum body.");

        var node = new EnumNode
        {
            NamespacePath = path,
            Name = name,
            UnderlyingType = underlyingType,
            Members = members
        };
        StampNode(node, startToken);
        return node;
    }

    private UnionNode ParseUnion()
    {
        var startToken = Expect(TokenType.Union);

        var (path, name) = ParseDottedDeclName(
            "Expected union name after 'union'.",
            "Expected identifier after '.' in union name.");

        Expect(TokenType.LBrace, "Expected '{' to start union body.");

        var variants = new List<UnionVariant>();
        while (Current.Type != TokenType.RBrace && Current.Type != TokenType.Eof)
        {
            var variantNameToken = Expect(TokenType.Identifier, "Expected union variant name.");
            Expect(TokenType.Colon, $"Expected ':' after union variant '{variantNameToken.Value}'.");
            var variantType = ParseType();
            var variant = new UnionVariant
            {
                Name = variantNameToken.Value,
                TypeName = variantType
            };
            StampNode(variant, variantNameToken);
            variants.Add(variant);

            if (Current.Type == TokenType.Comma)
            {
                Advance();
                continue;
            }

            if (Current.Type == TokenType.RBrace)
                break;

            ErrorReporter.Error(
                $"Expected ',' or '}}' after union variant '{variant.Name}', got {DescribeToken(Current)}.",
                Current.Line,
                Current.Column,
                _fileName);
            Advance();
        }

        Expect(TokenType.RBrace, "Expected '}' to close union body.");

        var node = new UnionNode
        {
            NamespacePath = path,
            Name = name,
            Variants = variants
        };
        StampNode(node, startToken);
        return node;
    }

    private VariableDeclarationNode ParseErrorDecl()
    {
        var startToken = Expect(TokenType.Error);
        var errorCode = Expect(TokenType.Identifier, "Expected error name after 'error'.").Value;
        Expect(TokenType.Equals, $"Expected '=' after error name '{errorCode}'.");
        var initializer = ParseExpression();

        var declaration = new VariableDeclarationNode
        {
            Name = $"Error_{errorCode}",
            TypeName = new TypeNode { Name = "i32" },
            Value = initializer,
            IsConst = true
        };
        StampNode(declaration, startToken);
        return declaration;
    }

    private TypeNode ParseType()
    {
        var isVolatile = MatchContextualKeyword("volatile", TokenType.Volatile);
        bool isSlice = false;
        int? arraySize = null;
        Expr? arraySizeExpr = null;

        if (Current.Type == TokenType.LBracket && Peek(1).Type == TokenType.RBracket)
        {
            Advance();
            Advance();
            isSlice = true;
        }
        else if (Current.Type == TokenType.LBracket)
        {
            Advance();
            arraySizeExpr = ParseExpression();
            if (arraySizeExpr is NumberExpr number && number.Value >= int.MinValue && number.Value <= int.MaxValue)
                arraySize = (int)number.Value;
            Expect(TokenType.RBracket, "Expected ']' after array size in type.");
        }

        if (Match(TokenType.Fn))
        {
            Expect(TokenType.LParen, "Expected '(' after 'fn' in function type.");
            var paramsList = new List<TypeNode>();
            if (Current.Type != TokenType.RParen)
            {
                do
                {
                    paramsList.Add(ParseType());
                } while (Match(TokenType.Comma));
            }
            Expect(TokenType.RParen, "Expected ')' to close function type parameter list.");

            TypeNode retType = new() { Name = "void" };
            if (Match(TokenType.Arrow))
                retType = ParseType();

            var fnTypeNode = new TypeNode { IsFunction = true, ParamTypes = paramsList, ReturnType = retType, IsSlice = isSlice, IsVolatile = isVolatile };
            if (arraySize.HasValue)
                fnTypeNode.ArraySize = arraySize;
            fnTypeNode.ArraySizeExpr = arraySizeExpr;
            return fnTypeNode;
        }

        bool isErrorUnion = Match(TokenType.Bang);

        bool pointer = false;
        int pointerLevel = 0;

        while (Match(TokenType.Star))
        {
            pointer = true;
            pointerLevel++;
        }

        var path = new List<string>();
        path.Add(Expect(TokenType.Identifier, "Expected type name.").Value);
        while (Match(TokenType.Dot))
            path.Add(Expect(TokenType.Identifier, "Expected identifier after '.' in type name.").Value);

        var name = path.Last();
        path.RemoveAt(path.Count - 1);

        if (Current.Type == TokenType.LBracket && Current.Line == Previous.Line)
        {
            ErrorReporter.Error(
                "Array and slice types must be written as '[N]T' or '[]T', not postfix forms like 'T[N]' or 'T[]'.",
                Current.Line,
                Current.Column,
                _fileName);
            throw new ZorbCompilerException("Invalid postfix array or slice type syntax.");
        }

        var typeNode = new TypeNode
        {
            Name = name,
            NamespacePath = path,
            IsVolatile = isVolatile,
            IsSlice = isSlice,
            IsPointer = pointer,
            PointerLevel = pointerLevel,
            ArraySize = arraySize,
            ArraySizeExpr = arraySizeExpr
        };

        if (!isErrorUnion)
            return typeNode;

        return new TypeNode
        {
            Name = typeNode.Name,
            NamespacePath = new List<string>(typeNode.NamespacePath),
            IsVolatile = typeNode.IsVolatile,
            IsSlice = typeNode.IsSlice,
            IsPointer = typeNode.IsPointer,
            PointerLevel = typeNode.PointerLevel,
            ArraySize = typeNode.ArraySize,
            ArraySizeExpr = typeNode.ArraySizeExpr,
            IsErrorUnion = true,
            ErrorInnerType = typeNode
        };
    }

    private FunctionDecl ParseFunction()
    {
        var attributes = ParseFunctionAttributes();

        var startToken = Current;
        bool isExtern = Match(TokenType.Extern);
        Expect(TokenType.Fn, "Expected 'fn' after 'extern'.");

        var path = new List<string>();
        path.Add(Expect(TokenType.Identifier, "Expected function name after 'fn'.").Value);
        while (Match(TokenType.Dot))
            path.Add(Expect(TokenType.Identifier, "Expected identifier after '.' in function name.").Value);

        var name = path.Last();
        path.RemoveAt(path.Count - 1);

        Expect(TokenType.LParen, "Expected '(' after function name.");

        var parameters = ParseParameters();

        Expect(TokenType.RParen, "Expected ')' to close parameter list.");

        TypeNode returnType = new() { Name = "void" };
        if (Current.Type == TokenType.Arrow)
        {
            Advance();
            returnType = ParseType();
        }
        else if (Current.Type == TokenType.Bang)
        {
            Advance();
            var innerType = ParseType();
            returnType = new TypeNode
            {
                Name = innerType.Name,
                NamespacePath = innerType.NamespacePath,
                IsVolatile = innerType.IsVolatile,
                IsSlice = innerType.IsSlice,
                IsPointer = innerType.IsPointer,
                PointerLevel = innerType.PointerLevel,
                ArraySize = innerType.ArraySize,
                ArraySizeExpr = innerType.ArraySizeExpr,
                IsErrorUnion = true,
                ErrorInnerType = innerType
            };
        }

        FunctionDecl function = new() { NamespacePath = path, Name = name, Parameters = parameters, ReturnType = returnType, IsExtern = isExtern, Attributes = attributes.Attributes, AlignExpr = attributes.AlignExpr };
        StampNode(function, startToken);

        if (isExtern)
            return function;

        Expect(TokenType.LBrace, "Expected '{' to start function body.");

        var body = new List<Statement>();
        while (Current.Type != TokenType.RBrace && Current.Type != TokenType.Eof)
            body.Add(ParseStatement());

        Expect(TokenType.RBrace, "Expected '}' to close function body.");
        function.Body = body;

        return function;
    }

    private List<Parameter> ParseParameters()
    {
        var parameters = new List<Parameter>();

        if (Current.Type != TokenType.RParen)
        {
            while (true)
            {
                var name = Expect(TokenType.Identifier, "Expected parameter name.").Value;
                Expect(TokenType.Colon, $"Expected ':' after parameter name '{name}'.");
                var typeName = ParseType();

                parameters.Add(new Parameter(name, typeName));

                if (Current.Type == TokenType.Comma)
                {
                    Advance();
                    continue;
                }

                break;
            }
        }

        return parameters;
    }

    private AttributeParseResult ParseAttributes()
    {
        var attributes = new List<string>();
        Expr? alignExpr = null;
        while (Current.Type == TokenType.LBracket)
        {
            if (Peek(1).Type == TokenType.Number)
                return new AttributeParseResult(attributes, alignExpr);

            Advance();
            while (Current.Type != TokenType.RBracket && Current.Type != TokenType.Eof)
            {
                if (Current.Type == TokenType.Align)
                {
                    Advance();
                    Expect(TokenType.LParen);
                    alignExpr = ParseExpression();
                    Expect(TokenType.RParen);
                }
                else if (MatchContextualKeyword("section", TokenType.Section))
                {
                    attributes.Add(ParseSectionAttribute());
                }
                else if (MatchContextualKeyword("volatile", TokenType.Volatile))
                {
                    attributes.Add("volatile");
                }
                else if (MatchContextualKeyword("noinline", TokenType.NoInline))
                {
                    attributes.Add("noinline");
                }
                else if (MatchContextualKeyword("noclone", TokenType.NoClone))
                {
                    attributes.Add("noclone");
                }
                else
                {
                    ErrorReporter.Error($"Unknown attribute {DescribeToken(Current)}.", Current.Line, Current.Column, _fileName);
                    Advance();
                }

                if (Current.Type == TokenType.Comma)
                {
                    Advance();
                }
                else if (Current.Type != TokenType.RBracket)
                {
                    ReportAttributeSeparatorError("attribute list");
                }
            }
            Expect(TokenType.RBracket, "Expected ']' to close attribute list.");
        }
        return new AttributeParseResult(attributes, alignExpr);
    }

    private AttributeParseResult ParseFunctionAttributes()
    {
        var attributes = new List<string>();
        Expr? alignExpr = null;
        while (Current.Type == TokenType.LBracket)
        {
            Advance();
            while (Current.Type != TokenType.RBracket && Current.Type != TokenType.Eof)
            {
                if (Current.Type == TokenType.Align)
                {
                    Advance();
                    Expect(TokenType.LParen);
                    alignExpr = ParseExpression();
                    Expect(TokenType.RParen);
                }
                else if (MatchContextualKeyword("abi", TokenType.Abi))
                {
                    Expect(TokenType.LParen, "Expected '(' after 'abi' attribute.");
                    var abiToken = Expect(TokenType.Identifier, "Expected ABI name inside abi(...).");
                    Expect(TokenType.RParen, "Expected ')' to close abi attribute.");
                    attributes.Add(ParseAbiAttribute(abiToken));
                }
                else if (MatchContextualKeyword("section", TokenType.Section))
                {
                    attributes.Add(ParseSectionAttribute());
                }
                else if (MatchContextualKeyword("noinline", TokenType.NoInline))
                {
                    attributes.Add("noinline");
                }
                else if (MatchContextualKeyword("noclone", TokenType.NoClone))
                {
                    attributes.Add("noclone");
                }
                else
                {
                    ErrorReporter.Error($"Unknown attribute {DescribeToken(Current)} in function attribute list.", Current.Line, Current.Column, _fileName);
                    Advance();
                }

                if (Current.Type == TokenType.Comma)
                {
                    Advance();
                }
                else if (Current.Type != TokenType.RBracket)
                {
                    ReportAttributeSeparatorError("function attribute list");
                }
            }
            Expect(TokenType.RBracket, "Expected ']' to close function attribute list.");
        }

        return new AttributeParseResult(attributes, alignExpr);
    }

    private AttributeParseResult ParseStructAttributes()
    {
        var attributes = new List<string>();
        Expr? alignExpr = null;
        while (Current.Type == TokenType.LBracket)
        {
            Advance();
            while (Current.Type != TokenType.RBracket && Current.Type != TokenType.Eof)
            {
                if (MatchContextualKeyword("packed", TokenType.Packed))
                {
                    attributes.Add("packed");
                }
                else if (MatchContextualKeyword("layout", TokenType.Layout))
                {
                    Expect(TokenType.LParen, "Expected '(' after 'layout' attribute.");
                    var layoutToken = Expect(TokenType.Identifier, "Expected layout kind inside layout(...).");
                    Expect(TokenType.RParen, "Expected ')' to close layout attribute.");
                    attributes.Add(ParseLayoutAttribute(layoutToken));
                }
                else if (Current.Type == TokenType.Align)
                {
                    Advance();
                    Expect(TokenType.LParen);
                    alignExpr = ParseExpression();
                    Expect(TokenType.RParen);
                }
                else
                {
                    ErrorReporter.Error($"Unknown attribute {DescribeToken(Current)} in struct attribute list.", Current.Line, Current.Column, _fileName);
                    Advance();
                }

                if (Current.Type == TokenType.Comma)
                {
                    Advance();
                }
                else if (Current.Type != TokenType.RBracket)
                {
                    ReportAttributeSeparatorError("struct attribute list");
                }
            }
            Expect(TokenType.RBracket, "Expected ']' to close struct attribute list.");
        }

        return new AttributeParseResult(attributes, alignExpr);
    }

    private FieldAttributeParseResult ParseFieldAttributes()
    {
        var attributes = new List<string>();
        Expr? offsetExpr = null;
        while (Current.Type == TokenType.LBracket)
        {
            Advance();
            while (Current.Type != TokenType.RBracket && Current.Type != TokenType.Eof)
            {
                if (MatchContextualKeyword("offset", TokenType.Offset))
                {
                    Expect(TokenType.LParen, "Expected '(' after 'offset' attribute.");
                    offsetExpr = ParseExpression();
                    Expect(TokenType.RParen, "Expected ')' to close offset attribute.");
                }
                else
                {
                    ErrorReporter.Error($"Unknown attribute {DescribeToken(Current)} in struct field attribute list.", Current.Line, Current.Column, _fileName);
                    Advance();
                }

                if (Current.Type == TokenType.Comma)
                {
                    Advance();
                }
                else if (Current.Type != TokenType.RBracket)
                {
                    ReportAttributeSeparatorError("struct field attribute list");
                }
            }
            Expect(TokenType.RBracket, "Expected ']' to close struct field attribute list.");
        }

        return new FieldAttributeParseResult(attributes, offsetExpr);
    }

    private string ParseAbiAttribute(Token abiToken)
    {
        return abiToken.Value switch
        {
            "sysv" or "sysv64" => "abi(sysv)",
            "ms" or "win64" => "abi(ms)",
            _ => ReportInvalidAbiAttribute(abiToken)
        };
    }

    private string ParseLayoutAttribute(Token layoutToken)
    {
        return layoutToken.Value switch
        {
            "explicit" => "layout(explicit)",
            _ => ReportInvalidLayoutAttribute(layoutToken)
        };
    }

    private string ReportInvalidLayoutAttribute(Token layoutToken)
    {
        ErrorReporter.Error($"Unknown layout '{layoutToken.Value}' in layout attribute. Expected: explicit.", layoutToken.Line, layoutToken.Column, _fileName);
        return $"layout({layoutToken.Value})";
    }

    private string ParseSectionAttribute()
    {
        Expect(TokenType.LParen, "Expected '(' after 'section' attribute.");
        var sectionToken = Expect(TokenType.String, "Expected string literal inside section(...).");
        Expect(TokenType.RParen, "Expected ')' to close section attribute.");
        return "section:" + sectionToken.Value;
    }

    private string ReportInvalidAbiAttribute(Token abiToken)
    {
        ErrorReporter.Error($"Unknown ABI '{abiToken.Value}' in abi attribute. Expected one of: sysv, sysv64, ms, win64.", abiToken.Line, abiToken.Column, _fileName);
        return $"abi({abiToken.Value})";
    }

    private static void ApplyVariableAttributes(VariableDeclarationNode varDecl, AttributeParseResult attributes)
    {
        varDecl.Attributes = attributes.Attributes;
        varDecl.AlignExpr = attributes.AlignExpr;
        if (attributes.Attributes.Contains("volatile"))
            varDecl.TypeName.IsVolatile = true;
    }
}
