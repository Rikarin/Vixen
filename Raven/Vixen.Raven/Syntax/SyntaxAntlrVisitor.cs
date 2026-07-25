using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using Vixen.Raven.Grammar;
using Vixen.Raven.Syntax.InternalSyntax;

namespace Vixen.Raven.Syntax;

public class SyntaxAntlrVisitor : RavenParser2BaseVisitor<SyntaxNode> {
    readonly ITokenStream? tokens;

    public SyntaxAntlrVisitor() { }

    public SyntaxAntlrVisitor(ITokenStream tokens) {
        this.tokens = tokens;
    }

    public override SyntaxNode VisitAliasQualifiedName(RavenParser2.AliasQualifiedNameContext context) {
        var identifier = Visit(context.identifier_name()) as IdentifierNameSyntax;
        var name = Visit(context.simple_name()) as SimpleNameSyntax;

        return SyntaxFactory.AliasQualifiedName(identifier!, name!);
    }

    public override SyntaxNode VisitQualifiedName(RavenParser2.QualifiedNameContext context) {
        var left = Visit(context.name()) as NameSyntax;
        var dot = Token(TerminalOf(context, RavenLexer2.DOT), SyntaxKind.DotToken);
        var right = Visit(context.simple_name()) as SimpleNameSyntax;

        return SyntaxFactory.QualifiedName(left!, dot, right!);
    }

    public override SyntaxNode VisitDefaultConstraint(RavenParser2.DefaultConstraintContext context) {
        var keyword = Token(TerminalOf(context, RavenLexer2.DEFAULT), SyntaxKind.DefaultKeyword);
        return SyntaxFactory.DefaultConstraint(keyword);
    }

    public override SyntaxNode VisitTypeContraint(RavenParser2.TypeContraintContext context) {
        var type = Visit(context.type()) as TypeSyntax;
        return SyntaxFactory.TypeConstraint(type!);
    }

    public override SyntaxNode VisitAssignmentExpression(RavenParser2.AssignmentExpressionContext context) {
        var left = Visit(context.expression(0)) as ExpressionSyntax;
        var op = Token(context.op, SyntaxKind.OperatorToken);
        var right = Visit(context.expression(1)) as ExpressionSyntax;
        return SyntaxFactory.AssignmentExpression(left!, op, right!);
    }

    public override SyntaxNode VisitBinaryExpression(RavenParser2.BinaryExpressionContext context) {
        var left = Visit(context.expression(0)) as ExpressionSyntax;
        var op = Token(context.op, SyntaxKind.OperatorToken);
        var right = Visit(context.expression(1)) as ExpressionSyntax;
        return SyntaxFactory.BinaryExpression(left!, op, right!);
    }

    public override SyntaxNode VisitCastExpression(RavenParser2.CastExpressionContext context) {
        var type = Visit(context.type()) as TypeSyntax;
        var expression = Visit(context.expression()) as ExpressionSyntax;

        return SyntaxFactory.CastExpression(type!, expression!);
    }

    public override SyntaxNode VisitCollectionExpression(RavenParser2.CollectionExpressionContext context) {
        var open = Token(TerminalOf(context, RavenLexer2.OPEN_BRACKET), SyntaxKind.OpenBracketToken);
        var elements = SeparatedList<CollectionElementSyntax>(
            context.collection_element().Select(Visit).ToArray(),
            Commas(context)
        );
        var close = Token(TerminalOf(context, RavenLexer2.CLOSE_BRACKET), SyntaxKind.CloseBracketToken);
        return SyntaxFactory.CollectionExpression(open, elements, close);
    }

    public override SyntaxNode VisitConditionalExpression(RavenParser2.ConditionalExpressionContext context) {
        var condition = Visit(context.expression(0)) as ExpressionSyntax;
        var question = Token(TerminalOf(context, RavenLexer2.INTERR), SyntaxKind.QuestionToken);
        var whenTrue = Visit(context.expression(1)) as ExpressionSyntax;
        var colon = Token(TerminalOf(context, RavenLexer2.COLON), SyntaxKind.ColonToken);
        var whenFalse = Visit(context.expression(2)) as ExpressionSyntax;
        return SyntaxFactory.ConditionalExpression(condition!, question, whenTrue!, colon, whenFalse!);
    }

    public override SyntaxNode VisitDeclarationExpression(RavenParser2.DeclarationExpressionContext context) {
        var type = Visit(context.type()) as TypeSyntax;
        var designation = Visit(context.variable_designation()) as VariableDesignationSyntax;
        return SyntaxFactory.DeclarationExpression(type!, designation!);
    }

    public override SyntaxNode VisitDefaultExpression(RavenParser2.DefaultExpressionContext context) {
        var type = Visit(context.type()) as TypeSyntax;
        return SyntaxFactory.DefaultExpression(type!);
    }

    public override SyntaxNode VisitElementAccessExpression(RavenParser2.ElementAccessExpressionContext context) {
        var expression = Visit(context.expression()) as ExpressionSyntax;
        var argumentList = Visit(context.bracketed_argument_list()) as BracketedArgumentListSyntax;
        return SyntaxFactory.ElementAccessExpression(expression!, argumentList!);
    }

    // NOTE: unreachable — a bare `[...]` always binds to #CollectionExpression, which
    // precedes #ImplicitElementAccess in the expression rule. Kept for grammar parity.
    public override SyntaxNode VisitImplicitElementAccess(RavenParser2.ImplicitElementAccessContext context) =>
        base.VisitImplicitElementAccess(context);

    public override SyntaxNode VisitInstanceExpression(RavenParser2.InstanceExpressionContext context) =>
        context.op.Type switch {
            RavenLexer2.BASE => SyntaxFactory.BaseExpression(),
            RavenLexer2.SELF => SyntaxFactory.SelfExpression(),
            _ => throw new NotSupportedException()
        };

    public override SyntaxNode VisitInvocationExpression(RavenParser2.InvocationExpressionContext context) {
        var expression = Visit(context.expression()) as ExpressionSyntax;
        var argumentList = Visit(context.argument_list()) as ArgumentListSyntax;
        return SyntaxFactory.InvocationExpression(expression!, argumentList!);
    }

    public override SyntaxNode VisitIsPatternExpression(RavenParser2.IsPatternExpressionContext context) {
        var expression = Visit(context.expression()) as ExpressionSyntax;
        var isKeyword = Token(TerminalOf(context, RavenLexer2.IS), SyntaxKind.IsKeyword);
        var pattern = Visit(context.pattern()) as PatternSyntax;

        return SyntaxFactory.IsPatternExpression(expression!, isKeyword, pattern!);
    }

    public override SyntaxNode VisitLiteralExpression(RavenParser2.LiteralExpressionContext context) {
        // literal_expression is a single terminal (or numeric_literal_token wrapping one).
        var literal = context.Start;
        var (expressionKind, tokenKind) = literal.Type switch {
            RavenLexer2.TRUE => (SyntaxKind.TrueLiteralExpression, SyntaxKind.TrueKeyword),
            RavenLexer2.FALSE => (SyntaxKind.FalseLiteralExpression, SyntaxKind.FalseKeyword),
            RavenLexer2.DEFAULT => (SyntaxKind.DefaultLiteralExpression, SyntaxKind.DefaultKeyword),
            RavenLexer2.STRING_LITERAL => (SyntaxKind.StringLiteralExpression, SyntaxKind.StringLiteralToken),
            _ => (SyntaxKind.NumericLiteralExpression, SyntaxKind.NumericLiteralToken)
        };

        return SyntaxFactory.LiteralExpression(expressionKind, Token(literal, tokenKind));
    }

    public override SyntaxNode VisitMemberAccessExpression(RavenParser2.MemberAccessExpressionContext context) {
        var expression = Visit(context.expression()) as ExpressionSyntax;
        var dot = Token(TerminalOf(context, RavenLexer2.DOT), SyntaxKind.DotToken);
        var name = Visit(context.simple_name()) as SimpleNameSyntax;
        return SyntaxFactory.MemberAccessExpression(expression!, dot, name!);
    }

    public override SyntaxNode VisitMemberBindingExpression(RavenParser2.MemberBindingExpressionContext context) {
        var name = Visit(context.simple_name()) as SimpleNameSyntax;
        return SyntaxFactory.MemberBindingExpression(name!);
    }

    public override SyntaxNode VisitParenthesizedExpression(RavenParser2.ParenthesizedExpressionContext context) {
        var open = Token(TerminalOf(context, RavenLexer2.OPEN_PARENS), SyntaxKind.OpenParenToken);
        var expression = Visit(context.expression()) as ExpressionSyntax;
        var close = Token(TerminalOf(context, RavenLexer2.CLOSE_PARENS), SyntaxKind.CloseParenToken);
        return SyntaxFactory.ParenthesizedExpression(open, expression!, close);
    }

    public override SyntaxNode VisitPostfixUnaryExpression(RavenParser2.PostfixUnaryExpressionContext context) {
        var kind = context.op.Type switch {
            RavenLexer2.OP_INC => SyntaxKind.PostIncrementExpression,
            RavenLexer2.OP_DEC => SyntaxKind.PostDecrementExpression,
            _ => throw new NotSupportedException()
        };
        var expression = Visit(context.expression()) as ExpressionSyntax;
        var op = Token(context.op, SyntaxKind.OperatorToken);

        return SyntaxFactory.PostfixUnaryExpression(kind, expression!, op);
    }

    public override SyntaxNode VisitPrefixUnaryExpression(RavenParser2.PrefixUnaryExpressionContext context) {
        var kind = context.op.Type switch {
            RavenLexer2.PLUS => SyntaxKind.UnaryPlusExpression,
            RavenLexer2.MINUS => SyntaxKind.UnaryMinusExpression,
            RavenLexer2.TILDE => SyntaxKind.BitwiseNotExpression,
            RavenLexer2.BANG => SyntaxKind.LogicalNotExpression,
            RavenLexer2.OP_INC => SyntaxKind.PreIncrementExpression,
            RavenLexer2.OP_DEC => SyntaxKind.PreDecrementExpression,
            RavenLexer2.CARET => SyntaxKind.IndexExpression,
            _ => throw new NotSupportedException()
        };
        var op = Token(context.op, SyntaxKind.OperatorToken);
        var expression = Visit(context.expression()) as ExpressionSyntax;

        return SyntaxFactory.PrefixUnaryExpression(kind, op, expression!);
    }

    public override SyntaxNode VisitRangeExpression(RavenParser2.RangeExpressionContext context) {
        var left = Visit(context.expression(0)) as ExpressionSyntax;
        var op = Token(TerminalOf(context, RavenLexer2.DOUBLE_DOT), SyntaxKind.DotDotToken);
        var right = Visit(context.expression(1)) as ExpressionSyntax;
        return SyntaxFactory.RangeExpression(left!, op, right!);
    }

    public override SyntaxNode VisitRefExpression(RavenParser2.RefExpressionContext context) {
        var refKeyword = Token(context.REF().Symbol, SyntaxKind.RefKeyword);
        var expression = Visit(context.expression()) as ExpressionSyntax;
        return SyntaxFactory.RefExpression(refKeyword, expression!);
    }

    public override SyntaxNode VisitSizeofExpression(RavenParser2.SizeofExpressionContext context) {
        var type = Visit(context.type()) as TypeSyntax;
        return SyntaxFactory.SizeOfExpression(type!);
    }

    public override SyntaxNode VisitSwitchExpression(RavenParser2.SwitchExpressionContext context) {
        var governing = Visit(context.expression()) as ExpressionSyntax;
        var switchKeyword = Token(TerminalOf(context, RavenLexer2.SWITCH), SyntaxKind.SwitchKeyword);
        var open = Token(TerminalOf(context, RavenLexer2.OPEN_BRACE), SyntaxKind.OpenBraceToken);
        var arms = SeparatedList<SwitchExpressionArmSyntax>(
            context.switch_expression_arm().Select(Visit).ToArray(),
            Commas(context)
        );
        var close = Token(TerminalOf(context, RavenLexer2.CLOSE_BRACE), SyntaxKind.CloseBraceToken);
        return SyntaxFactory.SwitchExpression(governing!, switchKeyword, open, arms, close);
    }

    public override SyntaxNode VisitTupleExpression(RavenParser2.TupleExpressionContext context) {
        var open = Token(TerminalOf(context, RavenLexer2.OPEN_PARENS), SyntaxKind.OpenParenToken);
        var arguments = SeparatedList<ArgumentSyntax>(context.argument().Select(Visit).ToArray(), Commas(context));
        var closeTerminal = TerminalOrNull(context, RavenLexer2.CLOSE_PARENS);
        var close = closeTerminal != null
            ? Token(closeTerminal, SyntaxKind.CloseParenToken)
            : SyntaxFactory.Token(SyntaxKind.CloseParenToken);
        return SyntaxFactory.TupleExpression(open, arguments, close);
    }

    // A bare type in expression position (e.g. a plain identifier) — TypeSyntax
    // derives from ExpressionSyntax, so the type node is already an expression.
    public override SyntaxNode VisitTypeExpression(RavenParser2.TypeExpressionContext context) => Visit(context.type());

    public override SyntaxNode VisitExpressionElement(RavenParser2.ExpressionElementContext context) {
        var expression = Visit(context.expression()) as ExpressionSyntax;
        return SyntaxFactory.ExpressionElement(expression!);
    }

    public override SyntaxNode VisitSpreadElement(RavenParser2.SpreadElementContext context) {
        var dotDot = Token(TerminalOf(context, RavenLexer2.DOUBLE_DOT), SyntaxKind.DotDotToken);
        var expression = Visit(context.expression()) as ExpressionSyntax;
        return SyntaxFactory.SpreadElement(dotDot, expression!);
    }

    public override SyntaxNode VisitBinaryPattern(RavenParser2.BinaryPatternContext context) {
        var left = Visit(context.pattern(0)) as PatternSyntax;
        var (kind, opKind) = context.op.Type == RavenLexer2.AND
            ? (SyntaxKind.AndPattern, SyntaxKind.AndKeyword)
            : (SyntaxKind.OrPattern, SyntaxKind.OrKeyword);
        var op = Token(context.op, opKind);
        var right = Visit(context.pattern(1)) as PatternSyntax;
        return SyntaxFactory.BinaryPattern(kind, left!, op, right!);
    }

    public override SyntaxNode VisitConstantPattern(RavenParser2.ConstantPatternContext context) {
        var expression = Visit(context.expression()) as ExpressionSyntax;
        return SyntaxFactory.ConstantPattern(expression!);
    }

    public override SyntaxNode VisitDiscardPattern(RavenParser2.DiscardPatternContext context) {
        var underscore = Token(TerminalOf(context, RavenLexer2.DISCARD), SyntaxKind.UnderscoreToken);
        return SyntaxFactory.DiscardPattern(underscore);
    }

    public override SyntaxNode VisitListPattern(RavenParser2.ListPatternContext context) {
        var open = Token(TerminalOf(context, RavenLexer2.OPEN_BRACKET), SyntaxKind.OpenBracketToken);
        var patterns = SeparatedList<PatternSyntax>(context.pattern().Select(Visit).ToArray(), Commas(context));
        var close = Token(TerminalOf(context, RavenLexer2.CLOSE_BRACKET), SyntaxKind.CloseBracketToken);
        var designation = context.variable_designation() != null
            ? Visit(context.variable_designation()) as VariableDesignationSyntax
            : null;
        return SyntaxFactory.ListPattern(open, patterns, close, designation);
    }

    public override SyntaxNode VisitParenthesizedPattern(RavenParser2.ParenthesizedPatternContext context) {
        var open = Token(TerminalOf(context, RavenLexer2.OPEN_PARENS), SyntaxKind.OpenParenToken);
        var pattern = Visit(context.pattern()) as PatternSyntax;
        var close = Token(TerminalOf(context, RavenLexer2.CLOSE_PARENS), SyntaxKind.CloseParenToken);
        return SyntaxFactory.ParenthesizedPattern(open, pattern!, close);
    }

    public override SyntaxNode VisitRelationalPattern(RavenParser2.RelationalPatternContext context) {
        var kind = context.op.Type switch {
            RavenLexer2.OP_EQ => SyntaxKind.EqualsRelationalPattern,
            RavenLexer2.OP_NE => SyntaxKind.NotEqualsRelationalPattern,
            RavenLexer2.LT => SyntaxKind.LessThanRelationalPattern,
            RavenLexer2.OP_LE => SyntaxKind.LessThanEqualsRelationalPattern,
            RavenLexer2.GT => SyntaxKind.GreaterThanRelationalPattern,
            RavenLexer2.OP_GE => SyntaxKind.GreaterThanEqualsRelationalPattern,
            _ => throw new NotSupportedException()
        };

        var op = Token(context.op, SyntaxKind.OperatorToken);
        var expression = Visit(context.expression()) as ExpressionSyntax;
        return SyntaxFactory.RelationalPattern(kind, op, expression!);
    }

    public override SyntaxNode VisitSlicePattern(RavenParser2.SlicePatternContext context) {
        var dotDot = Token(TerminalOf(context, RavenLexer2.DOUBLE_DOT), SyntaxKind.DotDotToken);
        var pattern = context.pattern() != null ? Visit(context.pattern()) as PatternSyntax : null;
        return SyntaxFactory.SlicePattern(dotDot, pattern);
    }

    public override SyntaxNode VisitUnaryPattern(RavenParser2.UnaryPatternContext context) {
        var op = Token(TerminalOf(context, RavenLexer2.NOT), SyntaxKind.NotKeyword);
        var pattern = Visit(context.pattern()) as PatternSyntax;
        return SyntaxFactory.UnaryPattern(op, pattern!);
    }

    public override SyntaxNode VisitVarPattern(RavenParser2.VarPatternContext context) {
        var keyword = Token(
            context.op,
            context.op.Type == RavenLexer2.VAR ? SyntaxKind.VarKeyword : SyntaxKind.ValKeyword
        );
        var designation = Visit(context.variable_designation()) as VariableDesignationSyntax;
        return SyntaxFactory.VarPattern(keyword, designation!);
    }

    public override SyntaxNode VisitDiscardDesignation(RavenParser2.DiscardDesignationContext context) {
        var underscore = Token(TerminalOf(context, RavenLexer2.DISCARD), SyntaxKind.UnderscoreToken);
        return SyntaxFactory.DiscardDesignation(underscore);
    }

    public override SyntaxNode VisitParenthesizedVariableDesignation(
        RavenParser2.ParenthesizedVariableDesignationContext context
    ) {
        var open = Token(TerminalOf(context, RavenLexer2.OPEN_PARENS), SyntaxKind.OpenParenToken);
        var variables = SeparatedList<VariableDesignationSyntax>(
            context.variable_designation().Select(Visit).ToArray(),
            Commas(context)
        );
        var close = Token(TerminalOf(context, RavenLexer2.CLOSE_PARENS), SyntaxKind.CloseParenToken);
        return SyntaxFactory.ParenthesizedVariableDesignation(open, variables, close);
    }

    public override SyntaxNode VisitSimpleVariableDesignation(RavenParser2.SimpleVariableDesignationContext context) {
        var identifier = Visit(context.identifier_token()) as SyntaxToken;
        return SyntaxFactory.SimpleVariableDesignation(identifier!);
    }

    public override SyntaxNode VisitArrayType(RavenParser2.ArrayTypeContext context) {
        var type = Visit(context.type()) as TypeSyntax;
        var rankSpecifiers = SyntaxList.List(context.array_rank_specifier().Select(Visit).ToArray());
        return SyntaxFactory.ArrayType(type!, new(rankSpecifiers));
    }

    // A name used as a type — the name node (IdentifierName/QualifiedName) is
    // itself a TypeSyntax.
    public override SyntaxNode VisitNameType(RavenParser2.NameTypeContext context) => Visit(context.name());

    public override SyntaxNode VisitPredefinedType(RavenParser2.PredefinedTypeContext context) =>
        context.pType.Type switch {
            RavenLexer2.BOOL => SyntaxFactory.PredefinedType(Token(context.pType, SyntaxKind.BoolKeyword)),
            RavenLexer2.BOOL2 => SyntaxFactory.PredefinedType(Token(context.pType, SyntaxKind.Bool2Keyword)),
            RavenLexer2.BOOL3 => SyntaxFactory.PredefinedType(Token(context.pType, SyntaxKind.Bool3Keyword)),
            RavenLexer2.BOOL4 => SyntaxFactory.PredefinedType(Token(context.pType, SyntaxKind.Bool4Keyword)),
            RavenLexer2.INT => SyntaxFactory.PredefinedType(Token(context.pType, SyntaxKind.IntKeyword)),
            RavenLexer2.INT2 => SyntaxFactory.PredefinedType(Token(context.pType, SyntaxKind.Int2Keyword)),
            RavenLexer2.INT3 => SyntaxFactory.PredefinedType(Token(context.pType, SyntaxKind.Int3Keyword)),
            RavenLexer2.INT4 => SyntaxFactory.PredefinedType(Token(context.pType, SyntaxKind.Int4Keyword)),
            RavenLexer2.UINT => SyntaxFactory.PredefinedType(Token(context.pType, SyntaxKind.UIntKeyword)),
            RavenLexer2.UINT2 => SyntaxFactory.PredefinedType(Token(context.pType, SyntaxKind.UInt2Keyword)),
            RavenLexer2.UINT3 => SyntaxFactory.PredefinedType(Token(context.pType, SyntaxKind.UInt3Keyword)),
            RavenLexer2.UINT4 => SyntaxFactory.PredefinedType(Token(context.pType, SyntaxKind.UInt4Keyword)),
            RavenLexer2.FLOAT => SyntaxFactory.PredefinedType(Token(context.pType, SyntaxKind.FloatKeyword)),
            RavenLexer2.FLOAT2 => SyntaxFactory.PredefinedType(Token(context.pType, SyntaxKind.Float2Keyword)),
            RavenLexer2.FLOAT3 => SyntaxFactory.PredefinedType(Token(context.pType, SyntaxKind.Float3Keyword)),
            RavenLexer2.FLOAT4 => SyntaxFactory.PredefinedType(Token(context.pType, SyntaxKind.Float4Keyword)),
            RavenLexer2.DOUBLE => SyntaxFactory.PredefinedType(Token(context.pType, SyntaxKind.DoubleKeyword)),
            RavenLexer2.DOUBLE2 => SyntaxFactory.PredefinedType(Token(context.pType, SyntaxKind.Double2Keyword)),
            RavenLexer2.DOUBLE3 => SyntaxFactory.PredefinedType(Token(context.pType, SyntaxKind.Double3Keyword)),
            RavenLexer2.DOUBLE4 => SyntaxFactory.PredefinedType(Token(context.pType, SyntaxKind.Double4Keyword)),
            RavenLexer2.MAT2 => SyntaxFactory.PredefinedType(Token(context.pType, SyntaxKind.Mat2Keyword)),
            RavenLexer2.MAT2X3 => SyntaxFactory.PredefinedType(Token(context.pType, SyntaxKind.Mat2x3Keyword)),
            RavenLexer2.MAT2X4 => SyntaxFactory.PredefinedType(Token(context.pType, SyntaxKind.Mat2x4Keyword)),
            RavenLexer2.MAT3 => SyntaxFactory.PredefinedType(Token(context.pType, SyntaxKind.Mat3Keyword)),
            RavenLexer2.MAT3X2 => SyntaxFactory.PredefinedType(Token(context.pType, SyntaxKind.Mat3x2Keyword)),
            RavenLexer2.MAT3X4 => SyntaxFactory.PredefinedType(Token(context.pType, SyntaxKind.Mat3x4Keyword)),
            RavenLexer2.MAT4 => SyntaxFactory.PredefinedType(Token(context.pType, SyntaxKind.Mat4Keyword)),
            RavenLexer2.MAT4X2 => SyntaxFactory.PredefinedType(Token(context.pType, SyntaxKind.Mat4x2Keyword)),
            RavenLexer2.MAT4X3 => SyntaxFactory.PredefinedType(Token(context.pType, SyntaxKind.Mat4x3Keyword)),
            _ => throw new NotSupportedException()
        };

    public override SyntaxNode VisitTupleType(RavenParser2.TupleTypeContext context) {
        var open = Token(TerminalOf(context, RavenLexer2.OPEN_PARENS), SyntaxKind.OpenParenToken);
        var elements = SeparatedList<TupleElementSyntax>(
            context.tuple_element().Select(Visit).ToArray(),
            Commas(context)
        );
        var close = Token(TerminalOf(context, RavenLexer2.CLOSE_PARENS), SyntaxKind.CloseParenToken);
        return SyntaxFactory.TupleType(open, elements, close);
    }

    public override SyntaxNode VisitCompilation_unit(RavenParser2.Compilation_unitContext context) {
        var package = Visit(context.package_declaration()) as PackageDirectiveSyntax;
        var imports = context.import_directive().Select(Visit).ToArray();
        var members = context.member_declaration().Select(Visit).ToArray();
        var endOfFile = Token(context.Stop, SyntaxKind.EndOfFileToken);

        return SyntaxFactory.CompilationUnit(
            package!,
            new(SyntaxList.List(imports)),
            new(SyntaxList.List(members)),
            endOfFile
        );
    }

    public override SyntaxNode VisitPackage_declaration(RavenParser2.Package_declarationContext context) {
        var packageKeyword = Token(context.PACKAGE().Symbol, SyntaxKind.PackageKeyword);
        var name = Visit(context.name()) as NameSyntax;
        return SyntaxFactory.PackageDirective(packageKeyword, name!);
    }

    public override SyntaxNode VisitImport_directive(RavenParser2.Import_directiveContext context) {
        var global = context.GLOBAL() != null ? Token(context.GLOBAL().Symbol, SyntaxKind.GlobalKeyword) : null;
        var importKeyword = Token(context.IMPORT().Symbol, SyntaxKind.ImportKeyword);
        var @static = context.STATIC() != null ? Token(context.STATIC().Symbol, SyntaxKind.StaticKeyword) : null;

        var name = Visit(context.name()) as NameSyntax;
        return SyntaxFactory.ImportDirective(global, importKeyword, @static, name!);
    }

    public override SyntaxNode VisitAttribute_list(RavenParser2.Attribute_listContext context) {
        var open = Token(TerminalOf(context, RavenLexer2.OPEN_BRACKET), SyntaxKind.OpenBracketToken);
        var target = context.attribute_target_specifier() != null
            ? Visit(context.attribute_target_specifier()) as AttributeTargetSpecifierSyntax
            : null;
        var attributes = SeparatedList<AttributeSyntax>(context.attribute().Select(Visit).ToArray(), Commas(context));
        var close = Token(TerminalOf(context, RavenLexer2.CLOSE_BRACKET), SyntaxKind.CloseBracketToken);
        return SyntaxFactory.AttributeList(open, target, attributes, close);
    }

    public override SyntaxNode VisitAttribute_target_specifier(RavenParser2.Attribute_target_specifierContext context) {
        // grammar: (type? | identifier_token?) ':' — the identifier form covers the
        // common targets (property:, field:, ...). A bare type target is rare; fall
        // back to its first terminal so we still round-trip.
        var identifier = context.identifier_token() != null
            ? Visit(context.identifier_token()) as SyntaxToken
            : context.type() != null
                ? Token(context.type().Start, SyntaxKind.IdentifierToken)
                : SyntaxFactory.Identifier(string.Empty);
        var colon = Token(TerminalOf(context, RavenLexer2.COLON), SyntaxKind.ColonToken);
        return SyntaxFactory.AttributeTargetSpecifier(identifier!, colon);
    }

    public override SyntaxNode VisitAttribute(RavenParser2.AttributeContext context) {
        var name = Visit(context.name()) as NameSyntax;
        var list = context.attribute_argument_list() != null
            ? Visit(context.attribute_argument_list()) as AttributeArgumentListSyntax
            : null;

        return SyntaxFactory.Attribute(name!, list);
    }

    public override SyntaxNode VisitAttribute_argument_list(RavenParser2.Attribute_argument_listContext context) {
        var open = Token(TerminalOf(context, RavenLexer2.OPEN_PARENS), SyntaxKind.OpenParenToken);
        var args = SeparatedList<AttributeArgumentSyntax>(
            context.attribute_argument().Select(Visit).ToArray(),
            Commas(context)
        );
        var close = Token(TerminalOf(context, RavenLexer2.CLOSE_PARENS), SyntaxKind.CloseParenToken);
        return SyntaxFactory.AttributeArgumentList(open, args, close);
    }

    public override SyntaxNode VisitAttribute_argument(RavenParser2.Attribute_argumentContext context) {
        NameColonSyntax? nameColonSyntax = null;

        if (context.name_colon() != null) {
            nameColonSyntax = Visit(context.name_colon()) as NameColonSyntax;
        }

        var expression = Visit(context.expression()) as ExpressionSyntax;
        return SyntaxFactory.AttributeArgument(nameColonSyntax, expression!);
    }

    public override SyntaxNode VisitParameter_list(RavenParser2.Parameter_listContext context) {
        var openParen = Token(TerminalOf(context, RavenLexer2.OPEN_PARENS), SyntaxKind.OpenParenToken);
        var parameters = context.parameter().Select(Visit).ToArray();
        var separated = SeparatedList<ParameterSyntax>(parameters, Commas(context));
        var closeParen = Token(TerminalOf(context, RavenLexer2.CLOSE_PARENS), SyntaxKind.CloseParenToken);
        return SyntaxFactory.ParameterList(openParen, separated, closeParen);
    }

    public override SyntaxNode VisitParameter(RavenParser2.ParameterContext context) {
        var attributes = context.attribute_list().Select(Visit).ToArray();
        var modifiers = context.modifier().Select(Visit).ToArray();
        var identifier = Visit(context.identifier_token()) as SyntaxToken;
        var colonToken = TerminalOrNull(context, RavenLexer2.COLON);
        var colon = colonToken != null ? Token(colonToken, SyntaxKind.ColonToken) : null;
        var type = context.type() != null ? Visit(context.type()) as TypeSyntax : null;
        var @default = context.equals_value_clause() != null
            ? Visit(context.equals_value_clause()) as EqualsValueClauseSyntax
            : null;

        return SyntaxFactory.Parameter(
            new(SyntaxList.List(attributes)),
            new(SyntaxList.List(modifiers)),
            identifier!,
            colon,
            type,
            @default
        );
    }

    public override SyntaxNode VisitGeneric_name(RavenParser2.Generic_nameContext context) {
        var identifier = Visit(context.identifier_token()) as SyntaxToken;
        var types = Visit(context.type_argument_list()) as TypeArgumentListSyntax;

        return SyntaxFactory.GenericName(identifier!, types!);
    }

    public override SyntaxNode VisitType_argument_list(RavenParser2.Type_argument_listContext context) {
        var lessThan = Token(TerminalOf(context, RavenLexer2.LT), SyntaxKind.LessThanToken);
        var args = SeparatedList<TypeSyntax>(context.type().Select(Visit).ToArray(), Commas(context));
        var greaterThan = Token(TerminalOf(context, RavenLexer2.GT), SyntaxKind.GreaterThanToken);
        return SyntaxFactory.TypeArgumentList(lessThan, args, greaterThan);
    }

    public override SyntaxNode VisitName_colon(RavenParser2.Name_colonContext context) {
        var identifier = Visit(context.identifier_name()) as IdentifierNameSyntax;
        var colon = Token(TerminalOf(context, RavenLexer2.COLON), SyntaxKind.ColonToken);
        return SyntaxFactory.NameColon(identifier!, colon);
    }

    public override SyntaxNode VisitIdentifier_name(RavenParser2.Identifier_nameContext context) {
        if (context.GLOBAL() != null) {
            throw new NotImplementedException();
        }

        var token = Visit(context.identifier_token()) as SyntaxToken;
        return SyntaxFactory.IdentifierName(token!);
    }

    public override SyntaxNode VisitField_declaration(RavenParser2.Field_declarationContext context) {
        var attributes = SyntaxList.List(context.attribute_list().Select(Visit).ToArray());
        var modifiers = SyntaxList.List(context.modifier().Select(Visit).ToArray());
        var declaration = Visit(context.variable_declaration()) as VariableDeclarationSyntax;

        return SyntaxFactory.FieldDeclaration(new(attributes), new(modifiers), declaration!);
    }

    public override SyntaxNode VisitConstructor_declaration(RavenParser2.Constructor_declarationContext context) {
        // NOTE: constructor_initializer (: base(...)) not yet modeled.
        var attributes = SyntaxList.List(context.attribute_list().Select(Visit).ToArray());
        var modifiers = SyntaxList.List(context.modifier().Select(Visit).ToArray());
        var keyword = Token(context.INIT().Symbol, SyntaxKind.InitKeyword);
        var parameters = Visit(context.parameter_list()) as ParameterListSyntax;
        var body = context.block() != null ? Visit(context.block()) as BlockSyntax : null;
        var expressionBody = context.arrow_expression_clause() != null
            ? Visit(context.arrow_expression_clause()) as ArrowExpressionClauseSyntax
            : null;

        return SyntaxFactory.ConstructorDeclaration(
            new(attributes),
            new(modifiers),
            keyword,
            parameters!,
            body,
            expressionBody
        );
    }

    public override SyntaxNode VisitConstructor_initializer(RavenParser2.Constructor_initializerContext context) {
        var kind = context.BASE() != null
            ? SyntaxKind.BaseConstructorInitializer
            : SyntaxKind.SelfConstructorInitializer;
        var arguments = Visit(context.argument_list()) as ArgumentListSyntax;

        return SyntaxFactory.ConstructorInitializer(kind, arguments!);
    }

    public override SyntaxNode VisitDestructor_declaration(RavenParser2.Destructor_declarationContext context) {
        var attributes = SyntaxList.List(context.attribute_list().Select(Visit).ToArray());
        var modifiers = SyntaxList.List(context.modifier().Select(Visit).ToArray());
        var tilde = Token(TerminalOf(context, RavenLexer2.TILDE), SyntaxKind.TildeToken);
        var keyword = Token(context.INIT().Symbol, SyntaxKind.InitKeyword);
        var parameters = Visit(context.parameter_list()) as ParameterListSyntax;
        var body = context.block() != null ? Visit(context.block()) as BlockSyntax : null;
        var expressionBody = context.arrow_expression_clause() != null
            ? Visit(context.arrow_expression_clause()) as ArrowExpressionClauseSyntax
            : null;

        return SyntaxFactory.DestructorDeclaration(
            new(attributes),
            new(modifiers),
            tilde,
            keyword,
            parameters!,
            body,
            expressionBody
        );
    }

    public override SyntaxNode VisitMethod_declaration(RavenParser2.Method_declarationContext context) {
        var attributes = SyntaxList.List(context.attribute_list().Select(Visit).ToArray());
        var modifiers = SyntaxList.List(context.modifier().Select(Visit).ToArray());
        var keyword = Token(context.FUNC().Symbol, SyntaxKind.FuncKeyword);
        var explicitInterface = context.explicit_interface_specifier() != null
            ? Visit(context.explicit_interface_specifier()) as ExplicitInterfaceSpecifierSyntax
            : null;
        var identifier = Visit(context.identifier_token()) as SyntaxToken;

        var typeParameters = context.type_parameter_list() != null
            ? Visit(context.type_parameter_list()) as TypeParameterListSyntax
            : null;
        var parameters = Visit(context.parameter_list()) as ParameterListSyntax;
        var constraints = SyntaxList.List(context.type_parameter_constraint_clause().Select(Visit).ToArray());

        var colonToken = TerminalOrNull(context, RavenLexer2.COLON);
        var colon = colonToken != null ? Token(colonToken, SyntaxKind.ColonToken) : null;
        var returnType = context.type() != null ? Visit(context.type()) as TypeSyntax : null;

        var body = context.block() != null ? Visit(context.block()) as BlockSyntax : null;
        var expressionBody = context.arrow_expression_clause() != null
            ? Visit(context.arrow_expression_clause()) as ArrowExpressionClauseSyntax
            : null;

        return SyntaxFactory.MethodDeclaration(
            new(attributes),
            new(modifiers),
            keyword,
            explicitInterface,
            identifier!,
            typeParameters,
            parameters!,
            new(constraints),
            colon,
            returnType,
            body,
            expressionBody
        );
    }

    public override SyntaxNode VisitExplicit_interface_specifier(
        RavenParser2.Explicit_interface_specifierContext context
    ) {
        var name = Visit(context.name()) as NameSyntax;
        var dot = Token(TerminalOf(context, RavenLexer2.DOT), SyntaxKind.DotToken);
        return SyntaxFactory.ExplicitInterfaceSpecifier(name!, dot);
    }

    public override SyntaxNode VisitProperty_declaration(RavenParser2.Property_declarationContext context) {
        var attributes = SyntaxList.List(context.attribute_list().Select(Visit).ToArray());
        var modifiers = SyntaxList.List(context.modifier().Select(Visit).ToArray());
        var keyword = Token(context.VAR().Symbol, SyntaxKind.VarKeyword);
        var explicitInterface = context.explicit_interface_specifier() != null
            ? Visit(context.explicit_interface_specifier()) as ExplicitInterfaceSpecifierSyntax
            : null;
        var identifier = Visit(context.identifier_token()) as SyntaxToken;
        var colonToken = TerminalOrNull(context, RavenLexer2.COLON);
        var colon = colonToken != null ? Token(colonToken, SyntaxKind.ColonToken) : null;
        var type = context.type() != null ? Visit(context.type()) as TypeSyntax : null;
        var accessorList = context.accessor_list() != null
            ? Visit(context.accessor_list()) as AccessorListSyntax
            : null;
        var expressionBody = context.arrow_expression_clause() != null
            ? Visit(context.arrow_expression_clause()) as ArrowExpressionClauseSyntax
            : null;
        var initializer = context.equals_value_clause() != null
            ? Visit(context.equals_value_clause()) as EqualsValueClauseSyntax
            : null;

        return SyntaxFactory.PropertyDeclaration(
            new(attributes),
            new(modifiers),
            keyword,
            explicitInterface,
            identifier!,
            colon,
            type,
            accessorList,
            expressionBody,
            initializer
        );
    }

    public override SyntaxNode VisitAccessor_list(RavenParser2.Accessor_listContext context) {
        var openBrace = Token(TerminalOf(context, RavenLexer2.OPEN_BRACE), SyntaxKind.OpenBraceToken);
        var accessors = SyntaxList.List(context.accessor_declaration().Select(Visit).ToArray());
        var closeBrace = Token(TerminalOf(context, RavenLexer2.CLOSE_BRACE), SyntaxKind.CloseBraceToken);
        return SyntaxFactory.AccessorList(openBrace, new(accessors), closeBrace);
    }

    public override SyntaxNode VisitAccessor_declaration(RavenParser2.Accessor_declarationContext context) {
        var attributes = SyntaxList.List(context.attribute_list().Select(Visit).ToArray());
        var modifiers = SyntaxList.List(context.modifier().Select(Visit).ToArray());
        var (kind, tokenKind) = context.op.Type switch {
            RavenLexer2.GET => (SyntaxKind.GetAccessorDeclaration, SyntaxKind.GetKeyword),
            RavenLexer2.SET => (SyntaxKind.SetAccessorDeclaration, SyntaxKind.SetKeyword),
            RavenLexer2.WILL_SET => (SyntaxKind.WillSetAccessorDeclaration, SyntaxKind.WillSetKeyword),
            RavenLexer2.DID_SET => (SyntaxKind.DidSetAccessorDeclaration, SyntaxKind.DidSetKeyword),
            _ => throw new NotSupportedException()
        };
        var keyword = Token(context.op, tokenKind);
        var body = context.block() != null ? Visit(context.block()) as BlockSyntax : null;
        var expressionBody = context.arrow_expression_clause() != null
            ? Visit(context.arrow_expression_clause()) as ArrowExpressionClauseSyntax
            : null;

        return SyntaxFactory.AccessorDeclaration(kind, new(attributes), new(modifiers), keyword, body, expressionBody);
    }


    public override SyntaxNode VisitIndexer_declaration(RavenParser2.Indexer_declarationContext context) {
        var attributes = SyntaxList.List(context.attribute_list().Select(Visit).ToArray());
        var modifiers = SyntaxList.List(context.modifier().Select(Visit).ToArray());
        var type = Visit(context.type()) as TypeSyntax;
        var explicitInterface = context.explicit_interface_specifier() != null
            ? Visit(context.explicit_interface_specifier()) as ExplicitInterfaceSpecifierSyntax
            : null;
        var self = Token(context.SELF().Symbol, SyntaxKind.SelfKeyword);
        var parameters = Visit(context.bracketed_parameter_list()) as BracketedParameterListSyntax;
        var accessorList = context.accessor_list() != null
            ? Visit(context.accessor_list()) as AccessorListSyntax
            : null;
        var expressionBody = context.arrow_expression_clause() != null
            ? Visit(context.arrow_expression_clause()) as ArrowExpressionClauseSyntax
            : null;

        return SyntaxFactory.IndexerDeclaration(
            new(attributes),
            new(modifiers),
            type!,
            explicitInterface,
            self,
            parameters!,
            accessorList,
            expressionBody
        );
    }

    public override SyntaxNode VisitBracketed_parameter_list(RavenParser2.Bracketed_parameter_listContext context) {
        var open = Token(TerminalOf(context, RavenLexer2.OPEN_BRACKET), SyntaxKind.OpenBracketToken);
        var parameters = SeparatedList<ParameterSyntax>(context.parameter().Select(Visit).ToArray(), Commas(context));
        var close = Token(TerminalOf(context, RavenLexer2.CLOSE_BRACKET), SyntaxKind.CloseBracketToken);
        return SyntaxFactory.BracketedParameterList(open, parameters, close);
    }

    public override SyntaxNode VisitConversion_operator_declaration(
        RavenParser2.Conversion_operator_declarationContext context
    ) {
        var attributes = SyntaxList.List(context.attribute_list().Select(Visit).ToArray());
        var modifiers = SyntaxList.List(context.modifier().Select(Visit).ToArray());
        var ctKeyword = Token(
            context.ct,
            context.ct.Type == RavenLexer2.IMPLICIT
                ? SyntaxKind.ImplicitKeyword
                : SyntaxKind.ExplicitKeyword
        );
        var explicitInterface = context.explicit_interface_specifier() != null
            ? Visit(context.explicit_interface_specifier()) as ExplicitInterfaceSpecifierSyntax
            : null;
        var operatorKeyword = Token(context.OPERATOR().Symbol, SyntaxKind.OperatorKeyword);
        var type = Visit(context.type()) as TypeSyntax;
        var parameters = Visit(context.parameter_list()) as ParameterListSyntax;
        var body = context.block() != null ? Visit(context.block()) as BlockSyntax : null;
        var expressionBody = context.arrow_expression_clause() != null
            ? Visit(context.arrow_expression_clause()) as ArrowExpressionClauseSyntax
            : null;

        return SyntaxFactory.ConversionOperatorDeclaration(
            new(attributes),
            new(modifiers),
            ctKeyword,
            explicitInterface,
            operatorKeyword,
            type!,
            parameters!,
            body,
            expressionBody
        );
    }

    public override SyntaxNode VisitOperator_declaration(RavenParser2.Operator_declarationContext context) {
        var attributes = SyntaxList.List(context.attribute_list().Select(Visit).ToArray());
        var modifiers = SyntaxList.List(context.modifier().Select(Visit).ToArray());
        var type = Visit(context.type()) as TypeSyntax;
        var explicitInterface = context.explicit_interface_specifier() != null
            ? Visit(context.explicit_interface_specifier()) as ExplicitInterfaceSpecifierSyntax
            : null;
        var operatorKeyword = Token(context.OPERATOR().Symbol, SyntaxKind.OperatorKeyword);
        var operatorToken = Token(context.op, SyntaxKind.OperatorToken);
        var parameters = Visit(context.parameter_list()) as ParameterListSyntax;
        var body = context.block() != null ? Visit(context.block()) as BlockSyntax : null;
        var expressionBody = context.arrow_expression_clause() != null
            ? Visit(context.arrow_expression_clause()) as ArrowExpressionClauseSyntax
            : null;

        return SyntaxFactory.OperatorDeclaration(
            new(attributes),
            new(modifiers),
            type!,
            explicitInterface,
            operatorKeyword,
            operatorToken,
            parameters!,
            body,
            expressionBody
        );
    }

    // member_declaration : <declaration> NL* — return the declaration, drop the
    // trailing newlines (they are consumed structurally / become trivia).
    public override SyntaxNode VisitMember_declaration(RavenParser2.Member_declarationContext context) =>
        Visit(context.GetChild(0));

    public override SyntaxNode VisitShader_declaration(RavenParser2.Shader_declarationContext context) {
        var attributes = SyntaxList.List(context.attribute_list().Select(Visit).ToArray());
        var modifiers = SyntaxList.List(context.modifier().Select(Visit).ToArray());
        var keyword = Token(context.SHADER().Symbol, SyntaxKind.ShaderKeyword);
        var identifier = Visit(context.identifier_token()) as SyntaxToken;

        var typeParameters = context.type_parameter_list() != null
            ? Visit(context.type_parameter_list()) as TypeParameterListSyntax
            : null;
        var parameters = context.parameter_list() != null
            ? Visit(context.parameter_list()) as ParameterListSyntax
            : null;
        var baseList = context.base_list() != null
            ? Visit(context.base_list()) as BaseListSyntax
            : null;
        var constraints = SyntaxList.List(context.type_parameter_constraint_clause().Select(Visit).ToArray());

        var openBraceToken = TerminalOrNull(context, RavenLexer2.OPEN_BRACE);
        var openBrace = openBraceToken != null ? Token(openBraceToken, SyntaxKind.OpenBraceToken) : null;
        var members = SyntaxList.List(context.member_declaration().Select(Visit).ToArray());
        var closeBraceToken = TerminalOrNull(context, RavenLexer2.CLOSE_BRACE);
        var closeBrace = closeBraceToken != null ? Token(closeBraceToken, SyntaxKind.CloseBraceToken) : null;

        return SyntaxFactory.ShaderDeclaration(
            new(attributes),
            new(modifiers),
            keyword,
            identifier!,
            typeParameters,
            parameters,
            baseList,
            new(constraints),
            openBrace,
            new(members),
            closeBrace
        );
    }

    public override SyntaxNode VisitStruct_declaration(RavenParser2.Struct_declarationContext context) {
        var attributes = SyntaxList.List(context.attribute_list().Select(Visit).ToArray());
        var modifiers = SyntaxList.List(context.modifier().Select(Visit).ToArray());
        var keyword = Token(context.STRUCT().Symbol, SyntaxKind.StructKeyword);
        var identifier = Visit(context.identifier_token()) as SyntaxToken;
        var typeParameters = context.type_parameter_list() != null
            ? Visit(context.type_parameter_list()) as TypeParameterListSyntax
            : null;
        var parameters = context.parameter_list() != null
            ? Visit(context.parameter_list()) as ParameterListSyntax
            : null;
        var baseList = context.base_list() != null ? Visit(context.base_list()) as BaseListSyntax : null;
        var constraints = SyntaxList.List(context.type_parameter_constraint_clause().Select(Visit).ToArray());
        var openBraceToken = TerminalOrNull(context, RavenLexer2.OPEN_BRACE);
        var openBrace = openBraceToken != null ? Token(openBraceToken, SyntaxKind.OpenBraceToken) : null;
        var members = SyntaxList.List(context.member_declaration().Select(Visit).ToArray());
        var closeBraceToken = TerminalOrNull(context, RavenLexer2.CLOSE_BRACE);
        var closeBrace = closeBraceToken != null ? Token(closeBraceToken, SyntaxKind.CloseBraceToken) : null;

        return SyntaxFactory.StructDeclaration(
            new(attributes),
            new(modifiers),
            keyword,
            identifier!,
            typeParameters,
            parameters,
            baseList,
            new(constraints),
            openBrace,
            new(members),
            closeBrace
        );
    }

    public override SyntaxNode VisitClass_declaration(RavenParser2.Class_declarationContext context) {
        var attributes = SyntaxList.List(context.attribute_list().Select(Visit).ToArray());
        var modifiers = SyntaxList.List(context.modifier().Select(Visit).ToArray());
        var keyword = Token(context.CLASS().Symbol, SyntaxKind.ClassKeyword);
        var identifier = Visit(context.identifier_token()) as SyntaxToken;
        var typeParameters = context.type_parameter_list() != null
            ? Visit(context.type_parameter_list()) as TypeParameterListSyntax
            : null;
        var parameters = context.parameter_list() != null
            ? Visit(context.parameter_list()) as ParameterListSyntax
            : null;
        var baseList = context.base_list() != null ? Visit(context.base_list()) as BaseListSyntax : null;
        var constraints = SyntaxList.List(context.type_parameter_constraint_clause().Select(Visit).ToArray());
        var openBraceToken = TerminalOrNull(context, RavenLexer2.OPEN_BRACE);
        var openBrace = openBraceToken != null ? Token(openBraceToken, SyntaxKind.OpenBraceToken) : null;
        var members = SyntaxList.List(context.member_declaration().Select(Visit).ToArray());
        var closeBraceToken = TerminalOrNull(context, RavenLexer2.CLOSE_BRACE);
        var closeBrace = closeBraceToken != null ? Token(closeBraceToken, SyntaxKind.CloseBraceToken) : null;

        return SyntaxFactory.ClassDeclaration(
            new(attributes),
            new(modifiers),
            keyword,
            identifier!,
            typeParameters,
            parameters,
            baseList,
            new(constraints),
            openBrace,
            new(members),
            closeBrace
        );
    }

    public override SyntaxNode VisitProtocol_declaration(RavenParser2.Protocol_declarationContext context) {
        var attributes = SyntaxList.List(context.attribute_list().Select(Visit).ToArray());
        var modifiers = SyntaxList.List(context.modifier().Select(Visit).ToArray());
        var keyword = Token(context.PROTOCOL().Symbol, SyntaxKind.ProtocolKeyword);
        var identifier = Visit(context.identifier_token()) as SyntaxToken;
        var typeParameters = context.type_parameter_list() != null
            ? Visit(context.type_parameter_list()) as TypeParameterListSyntax
            : null;
        var parameters = context.parameter_list() != null
            ? Visit(context.parameter_list()) as ParameterListSyntax
            : null;
        var baseList = context.base_list() != null ? Visit(context.base_list()) as BaseListSyntax : null;
        var constraints = SyntaxList.List(context.type_parameter_constraint_clause().Select(Visit).ToArray());
        var openBraceToken = TerminalOrNull(context, RavenLexer2.OPEN_BRACE);
        var openBrace = openBraceToken != null ? Token(openBraceToken, SyntaxKind.OpenBraceToken) : null;
        var members = SyntaxList.List(context.member_declaration().Select(Visit).ToArray());
        var closeBraceToken = TerminalOrNull(context, RavenLexer2.CLOSE_BRACE);
        var closeBrace = closeBraceToken != null ? Token(closeBraceToken, SyntaxKind.CloseBraceToken) : null;

        return SyntaxFactory.ProtocolDeclaration(
            new(attributes),
            new(modifiers),
            keyword,
            identifier!,
            typeParameters,
            parameters,
            baseList,
            new(constraints),
            openBrace,
            new(members),
            closeBrace
        );
    }

    public override SyntaxNode VisitEnum_declaration(RavenParser2.Enum_declarationContext context) {
        var attributes = SyntaxList.List(context.attribute_list().Select(Visit).ToArray());
        var modifiers = SyntaxList.List(context.modifier().Select(Visit).ToArray());
        var keyword = Token(context.ENUM().Symbol, SyntaxKind.EnumKeyword);
        var identifier = Visit(context.identifier_token()) as SyntaxToken;
        var baseList = context.base_list() != null ? Visit(context.base_list()) as BaseListSyntax : null;
        var openBrace = Token(TerminalOf(context, RavenLexer2.OPEN_BRACE), SyntaxKind.OpenBraceToken);
        var members = SeparatedList<EnumMemberDeclarationSyntax>(
            context.enum_member_declaration().Select(Visit).ToArray(),
            Commas(context)
        );
        var closeBrace = Token(TerminalOf(context, RavenLexer2.CLOSE_BRACE), SyntaxKind.CloseBraceToken);

        return SyntaxFactory.EnumDeclaration(
            new(attributes),
            new(modifiers),
            keyword,
            identifier!,
            baseList,
            openBrace,
            members,
            closeBrace
        );
    }

    public override SyntaxNode VisitEnum_member_declaration(RavenParser2.Enum_member_declarationContext context) {
        var attributes = SyntaxList.List(context.attribute_list().Select(Visit).ToArray());
        var modifiers = SyntaxList.List(context.modifier().Select(Visit).ToArray());
        var identifier = Visit(context.identifier_token()) as SyntaxToken;
        var value = context.equals_value_clause() != null
            ? Visit(context.equals_value_clause()) as EqualsValueClauseSyntax
            : null;

        return SyntaxFactory.EnumMemberDeclaration(new(attributes), new(modifiers), identifier!, value);
    }

    public override SyntaxNode VisitType_parameter_list(RavenParser2.Type_parameter_listContext context) {
        var lessThan = Token(TerminalOf(context, RavenLexer2.LT), SyntaxKind.LessThanToken);
        var typeParams = SeparatedList<TypeParameterSyntax>(
            context.type_parameter().Select(Visit).ToArray(),
            Commas(context)
        );
        var greaterThan = Token(TerminalOf(context, RavenLexer2.GT), SyntaxKind.GreaterThanToken);
        return SyntaxFactory.TypeParameterList(lessThan, typeParams, greaterThan);
    }

    public override SyntaxNode VisitType_parameter(RavenParser2.Type_parameterContext context) {
        var attributes = SyntaxList.List(context.attribute_list().Select(Visit).ToArray());
        var variance = context.variance != null
            ? Token(
                context.variance,
                context.variance.Type == RavenLexer2.IN ? SyntaxKind.InKeyword : SyntaxKind.OutKeyword
            )
            : null;
        var identifier = Visit(context.identifier_token()) as SyntaxToken;

        return SyntaxFactory.TypeParameter(new(attributes), variance, identifier!);
    }

    public override SyntaxNode VisitType_parameter_constraint_clause(
        RavenParser2.Type_parameter_constraint_clauseContext context
    ) {
        var where = Token(TerminalOf(context, RavenLexer2.WHERE), SyntaxKind.WhereKeyword);
        var name = Visit(context.identifier_name()) as IdentifierNameSyntax;
        var colon = Token(TerminalOf(context, RavenLexer2.COLON), SyntaxKind.ColonToken);
        var constraints = SeparatedList<TypeParameterConstraintSyntax>(
            context.type_parameter_constraint().Select(Visit).ToArray(),
            Commas(context)
        );
        return SyntaxFactory.TypeParameterConstraintClause(where, name!, colon, constraints);
    }

    public override SyntaxNode VisitType_parameter_constraint(RavenParser2.Type_parameter_constraintContext context) =>
        Visit(context.GetChild(0));

    public override SyntaxNode VisitBase_list(RavenParser2.Base_listContext context) {
        var colon = Token(TerminalOf(context, RavenLexer2.COLON), SyntaxKind.ColonToken);
        var types = context.base_type().Select(Visit).ToArray();
        var separated = SeparatedList<BaseTypeSyntax>(types, Commas(context));
        return SyntaxFactory.BaseList(colon, separated);
    }

    public override SyntaxNode VisitPrimary_constructor_base_type(
        RavenParser2.Primary_constructor_base_typeContext context
    ) {
        var type = Visit(context.type()) as TypeSyntax;
        var args = Visit(context.argument_list()) as ArgumentListSyntax;
        return SyntaxFactory.PrimaryConstructorBaseType(type!, args!);
    }

    public override SyntaxNode VisitSimple_base_type(RavenParser2.Simple_base_typeContext context) {
        var type = Visit(context.type()) as TypeSyntax;
        return SyntaxFactory.SimpleBaseType(type!);
    }

    public override SyntaxNode VisitVariable_declaration(RavenParser2.Variable_declarationContext context) {
        var isVar = context.VAR() != null;
        var kind = isVar ? SyntaxKind.VariableDeclaration : SyntaxKind.ConstDeclaration;
        var keyword = isVar
            ? Token(context.VAR().Symbol, SyntaxKind.VarKeyword)
            : Token(context.VAL().Symbol, SyntaxKind.ValKeyword);
        var identifier = Visit(context.identifier_token()) as SyntaxToken;

        var colonToken = TerminalOrNull(context, RavenLexer2.COLON);
        var colon = colonToken != null ? Token(colonToken, SyntaxKind.ColonToken) : null;
        var type = context.type() != null ? Visit(context.type()) as TypeSyntax : null;

        var initializer = context.equals_value_clause() != null
            ? Visit(context.equals_value_clause()) as EqualsValueClauseSyntax
            : null;

        return SyntaxFactory.VariableDeclaration(kind, keyword, identifier!, colon, type, initializer);
    }

    public override SyntaxNode VisitArgument_list(RavenParser2.Argument_listContext context) {
        // NOTE: separators (commas) between arguments are not yet modeled — full
        // round-trip of multi-argument calls needs a separated list (future work).
        var open = Token(TerminalOf(context, RavenLexer2.OPEN_PARENS), SyntaxKind.OpenParenToken);
        var args = context.argument().Select(Visit).ToArray();
        var separated = SeparatedList<ArgumentSyntax>(args, Commas(context));
        var close = Token(TerminalOf(context, RavenLexer2.CLOSE_PARENS), SyntaxKind.CloseParenToken);
        return SyntaxFactory.ArgumentList(open, separated, close);
    }

    public override SyntaxNode VisitArgument(RavenParser2.ArgumentContext context) {
        var nameColon = context.name_colon() != null
            ? Visit(context.name_colon()) as NameColonSyntax
            : null;

        var refKind = context.kind?.Type switch {
            RavenLexer2.REF => Token(context.kind, SyntaxKind.RefKeyword),
            RavenLexer2.OUT => Token(context.kind, SyntaxKind.OutKeyword),
            RavenLexer2.IN => Token(context.kind, SyntaxKind.InKeyword),
            _ => null
        };

        var expression = Visit(context.expression()) as ExpressionSyntax;
        return SyntaxFactory.Argument(nameColon, refKind, expression!);
    }

    public override SyntaxNode VisitBracketed_argument_list(RavenParser2.Bracketed_argument_listContext context) {
        var open = Token(TerminalOf(context, RavenLexer2.OPEN_BRACKET), SyntaxKind.OpenBracketToken);
        var args = context.argument().Select(Visit).ToArray();
        var separated = SeparatedList<ArgumentSyntax>(args, Commas(context));
        var close = Token(TerminalOf(context, RavenLexer2.CLOSE_BRACKET), SyntaxKind.CloseBracketToken);
        return SyntaxFactory.BracketedArgumentList(open, separated, close);
    }

    public override SyntaxNode VisitBlock(RavenParser2.BlockContext context) {
        var openBrace = Token(TerminalOf(context, RavenLexer2.OPEN_BRACE), SyntaxKind.OpenBraceToken);
        var statements = context.statement().Select(Visit).ToArray();
        var closeBrace = Token(TerminalOf(context, RavenLexer2.CLOSE_BRACE), SyntaxKind.CloseBraceToken);
        return SyntaxFactory.Block(new(), openBrace, new(SyntaxList.List(statements)), closeBrace);
    }

    public override SyntaxNode VisitBreak_statement(RavenParser2.Break_statementContext context) {
        var attributes = context.attribute_list().Select(Visit).ToArray();
        return SyntaxFactory.BreakStatement(new(SyntaxList.List(attributes)));
    }

    public override SyntaxNode VisitContinue_statement(RavenParser2.Continue_statementContext context) {
        var attributes = context.attribute_list().Select(Visit).ToArray();
        return SyntaxFactory.ContinueStatement(new(SyntaxList.List(attributes)));
    }

    public override SyntaxNode VisitRepeat_statement(RavenParser2.Repeat_statementContext context) {
        var attributes = SyntaxList.List(context.attribute_list().Select(Visit).ToArray());
        var expression = Visit(context.expression()) as ExpressionSyntax;
        var statement = Visit(context.statement()) as StatementSyntax;

        return SyntaxFactory.RepeatStatement(new(attributes), statement!, expression!);
    }

    public override SyntaxNode VisitEmpty_statement(RavenParser2.Empty_statementContext context) {
        var attributes = SyntaxList.List(context.attribute_list().Select(Visit).ToArray());
        return SyntaxFactory.EmptyStatement(new(attributes));
    }

    public override SyntaxNode VisitExpression_statement(RavenParser2.Expression_statementContext context) {
        var attributes = SyntaxList.List(context.attribute_list().Select(Visit).ToArray());
        var expression = Visit(context.expression()) as ExpressionSyntax;

        return SyntaxFactory.ExpressionStatement(new(attributes), expression!);
    }

    public override SyntaxNode VisitFor_statement(RavenParser2.For_statementContext context) {
        var attributes = SyntaxList.List(context.attribute_list().Select(Visit).ToArray());
        var forKeyword = Token(context.FOR().Symbol, SyntaxKind.ForKeyword);
        var openParen = Token(TerminalOf(context, RavenLexer2.OPEN_PARENS), SyntaxKind.OpenParenToken);
        var identifier = Visit(context.identifier_token()) as SyntaxToken;
        var inKeyword = Token(context.IN().Symbol, SyntaxKind.InKeyword);
        var expression = Visit(context.expression()) as ExpressionSyntax;
        var closeParen = Token(TerminalOf(context, RavenLexer2.CLOSE_PARENS), SyntaxKind.CloseParenToken);
        var block = Visit(context.block()) as StatementSyntax;

        return SyntaxFactory.ForStatement(
            new(attributes),
            forKeyword,
            openParen,
            identifier!,
            inKeyword,
            expression!,
            closeParen,
            block!
        );
    }

    public override SyntaxNode VisitIf_statement(RavenParser2.If_statementContext context) {
        var attributes = SyntaxList.List(context.attribute_list().Select(Visit).ToArray());
        var ifKeyword = Token(context.IF().Symbol, SyntaxKind.IfKeyword);
        var openParen = Token(TerminalOf(context, RavenLexer2.OPEN_PARENS), SyntaxKind.OpenParenToken);
        var expression = Visit(context.expression()) as ExpressionSyntax;
        var closeParen = Token(TerminalOf(context, RavenLexer2.CLOSE_PARENS), SyntaxKind.CloseParenToken);
        var block = Visit(context.block()) as StatementSyntax;
        var elseClause = context.else_clause() != null
            ? Visit(context.else_clause()) as ElseClauseSyntax
            : null;

        return SyntaxFactory.IfStatement(
            new(attributes),
            ifKeyword,
            openParen,
            expression!,
            closeParen,
            block!,
            elseClause
        );
    }

    public override SyntaxNode VisitElse_clause(RavenParser2.Else_clauseContext context) {
        var elseKeyword = Token(context.ELSE().Symbol, SyntaxKind.ElseKeyword);
        var block = Visit(context.block()) as StatementSyntax;
        return SyntaxFactory.ElseClause(elseKeyword, block!);
    }

    public override SyntaxNode VisitReturn_statement(RavenParser2.Return_statementContext context) {
        var attributes = SyntaxList.List(context.attribute_list().Select(Visit).ToArray());
        var returnKeyword = Token(context.RETURN().Symbol, SyntaxKind.ReturnKeyword);
        var expression = context.expression() != null ? Visit(context.expression()) as ExpressionSyntax : null;

        return SyntaxFactory.ReturnStatement(new(attributes), returnKeyword, expression);
    }

    public override SyntaxNode VisitLocal_function_statement(RavenParser2.Local_function_statementContext context) {
        var attributes = SyntaxList.List(context.attribute_list().Select(Visit).ToArray());
        var modifiers = SyntaxList.List(context.modifier().Select(Visit).ToArray());
        var keyword = Token(context.FUNC().Symbol, SyntaxKind.FuncKeyword);
        var identifier = Visit(context.identifier_token()) as SyntaxToken;
        var typeParameters = context.type_parameter_list() != null
            ? Visit(context.type_parameter_list()) as TypeParameterListSyntax
            : null;
        var parameters = Visit(context.parameter_list()) as ParameterListSyntax;
        var constraints = SyntaxList.List(context.type_parameter_constraint_clause().Select(Visit).ToArray());
        var colonToken = TerminalOrNull(context, RavenLexer2.COLON);
        var colon = colonToken != null ? Token(colonToken, SyntaxKind.ColonToken) : null;
        var returnType = context.type() != null ? Visit(context.type()) as TypeSyntax : null;
        var block = context.block() != null ? Visit(context.block()) as BlockSyntax : null;
        var expression = context.arrow_expression_clause() != null
            ? Visit(context.arrow_expression_clause()) as ArrowExpressionClauseSyntax
            : null;

        return SyntaxFactory.LocalFunctionStatement(
            new(attributes),
            new(modifiers),
            keyword,
            identifier!,
            typeParameters,
            parameters!,
            new(constraints),
            colon,
            returnType,
            block,
            expression
        );
    }

    public override SyntaxNode VisitLocal_declaration_statement(RavenParser2.Local_declaration_statementContext context) {
        var attributes = SyntaxList.List(context.attribute_list().Select(Visit).ToArray());
        var @using = context.USING() != null ? SyntaxFactory.Token(SyntaxKind.UsingKeyword) : null;
        var modifiers = context.modifier().Select(Visit).ToArray();
        var declaration = context.variable_declaration() != null
            ? Visit(context.variable_declaration()) as VariableDeclarationSyntax
            : null;

        return SyntaxFactory.LocalDeclarationStatement(
            new(attributes),
            @using,
            new(SyntaxList.List(modifiers)),
            declaration!
        );
    }

    public override SyntaxNode VisitWhile_statement(RavenParser2.While_statementContext context) {
        var attributes = SyntaxList.List(context.attribute_list().Select(Visit).ToArray());
        var whileKeyword = Token(context.WHILE().Symbol, SyntaxKind.WhileKeyword);
        var openParen = Token(TerminalOf(context, RavenLexer2.OPEN_PARENS), SyntaxKind.OpenParenToken);
        var expression = Visit(context.expression()) as ExpressionSyntax;
        var closeParen = Token(TerminalOf(context, RavenLexer2.CLOSE_PARENS), SyntaxKind.CloseParenToken);
        var block = Visit(context.statement()) as StatementSyntax;

        return SyntaxFactory.WhileStatement(new(attributes), whileKeyword, openParen, expression!, closeParen, block!);
    }

    public override SyntaxNode VisitUsing_statement(RavenParser2.Using_statementContext context) {
        var attributes = SyntaxList.List(context.attribute_list().Select(Visit).ToArray());
        var declaration = context.variable_declaration() != null
            ? Visit(context.variable_declaration()) as VariableDeclarationSyntax
            : null;
        var expression = context.expression() != null ? Visit(context.expression()) as ExpressionSyntax : null;
        var block = Visit(context.statement()) as StatementSyntax;

        return SyntaxFactory.UsingStatement(new(attributes), declaration, expression, block!);
    }

    public override SyntaxNode VisitSwitch_statement(RavenParser2.Switch_statementContext context) {
        var attributes = SyntaxList.List(context.attribute_list().Select(Visit).ToArray());
        var expression = Visit(context.expression()) as ExpressionSyntax;
        var sections = SyntaxList.List(context.switch_section().Select(Visit).ToArray());

        return SyntaxFactory.SwitchStatement(new(attributes), expression!, new(sections));
    }

    public override SyntaxNode VisitSwitch_section(RavenParser2.Switch_sectionContext context) {
        var labels = context.switch_label().Select(Visit).ToArray();
        var statements = context.statement().Select(Visit).ToArray();

        return SyntaxFactory.SwitchSection(new(SyntaxList.List(labels)), new(SyntaxList.List(statements)));
    }

    public override SyntaxNode VisitCase_pattern_switch_label(RavenParser2.Case_pattern_switch_labelContext context) {
        var pattern = Visit(context.pattern()) as PatternSyntax;
        var whenClause = context.when_clause() != null ? Visit(context.when_clause()) as WhenClauseSyntax : null;
        return SyntaxFactory.CasePatternSwitchLabel(pattern!, whenClause!);
    }

    public override SyntaxNode VisitCase_switch_label(RavenParser2.Case_switch_labelContext context) {
        var expression = context.expression() != null ? Visit(context.expression()) as ExpressionSyntax : null;
        return SyntaxFactory.CaseSwitchLabel(expression!);
    }

    public override SyntaxNode VisitDefault_switch_label(RavenParser2.Default_switch_labelContext context) =>
        SyntaxFactory.DefaultSwitchLabel();

    // NOTE: never dispatched — literals reach the tree via the labeled #LiteralExpression
    // alternative (VisitLiteralExpression), not this bare rule visitor.
    public override SyntaxNode VisitLiteral_expression(RavenParser2.Literal_expressionContext context) =>
        base.VisitLiteral_expression(context);

    public override SyntaxNode VisitEquals_value_clause(RavenParser2.Equals_value_clauseContext context) {
        var equals = Token(TerminalOf(context, RavenLexer2.ASSIGNMENT), SyntaxKind.EqualsToken);
        var expression = Visit(context.expression()) as ExpressionSyntax;
        return SyntaxFactory.EqualsValueClause(equals, expression!);
    }

    public override SyntaxNode VisitArrow_expression_clause(RavenParser2.Arrow_expression_clauseContext context) {
        var arrow = Token(TerminalOf(context, RavenLexer2.LAMBDA), SyntaxKind.ArrowToken);
        var expression = Visit(context.expression()) as ExpressionSyntax;
        return SyntaxFactory.ArrowExpressionClause(arrow, expression!);
    }

    // NOTE: never dispatched — elements reach the tree via the labeled #ExpressionElement /
    // #SpreadElement alternatives, not this bare rule visitor.
    public override SyntaxNode VisitCollection_element(RavenParser2.Collection_elementContext context) =>
        base.VisitCollection_element(context);

    public override SyntaxNode VisitSwitch_expression_arm(RavenParser2.Switch_expression_armContext context) {
        var pattern = Visit(context.pattern()) as PatternSyntax;
        var when = context.when_clause() != null ? Visit(context.when_clause()) as WhenClauseSyntax : null;
        var arrow = Token(TerminalOf(context, RavenLexer2.LAMBDA), SyntaxKind.ArrowToken);
        var expression = Visit(context.expression()) as ExpressionSyntax;
        return SyntaxFactory.SwitchExpressionArm(pattern!, when, arrow, expression!);
    }

    public override SyntaxNode VisitVariable_designation(RavenParser2.Variable_designationContext context) =>
        Visit(context.GetChild(0));

    public override SyntaxNode VisitWhen_clause(RavenParser2.When_clauseContext context) {
        var whenKeyword = Token(TerminalOf(context, RavenLexer2.WHEN), SyntaxKind.WhenKeyword);
        var condition = Visit(context.expression()) as ExpressionSyntax;
        return SyntaxFactory.WhenClause(whenKeyword, condition!);
    }

    public override SyntaxNode VisitType(RavenParser2.TypeContext context) => base.VisitType(context);

    public override SyntaxNode VisitTuple_element(RavenParser2.Tuple_elementContext context) {
        var type = Visit(context.type()) as TypeSyntax;
        var identifier = context.identifier_token() != null ? Visit(context.identifier_token()) as SyntaxToken : null;

        return SyntaxFactory.TupleElement(type!, identifier);
    }

    public override SyntaxNode VisitArray_rank_specifier(RavenParser2.Array_rank_specifierContext context) {
        var open = Token(TerminalOf(context, RavenLexer2.OPEN_BRACKET), SyntaxKind.OpenBracketToken);
        var commas = SyntaxList.List(Commas(context).Cast<SyntaxNode>().ToArray());
        var close = Token(TerminalOf(context, RavenLexer2.CLOSE_BRACKET), SyntaxKind.CloseBracketToken);
        return SyntaxFactory.ArrayRankSpecifier(open, new(commas), close);
    }

    public override SyntaxNode VisitIdentifier_token(RavenParser2.Identifier_tokenContext context) =>
        // TODO: verbatim '@' identifiers not yet modeled as part of the token text.
        Token(context.IDENTIFIER().Symbol, SyntaxKind.IdentifierToken);

    public override SyntaxNode VisitReal_literal_token(RavenParser2.Real_literal_tokenContext context) {
        var text = context.REAL_LITERAL().GetText();
        if (text.EndsWith('f')) {
            return SyntaxFactory.Literal(float.Parse(context.REAL_LITERAL().GetText()[..^1]));
        }

        return SyntaxFactory.Literal(double.Parse(context.REAL_LITERAL().GetText()));
    }

    public override SyntaxNode VisitInteger_literal_token(RavenParser2.Integer_literal_tokenContext context) {
        // TODO: how to parse ulong, etc?
        var type = context.GetChild(0) as ITerminalNode;

        return type?.Symbol.Type switch {
            RavenLexer2.INTEGER_LITERAL => SyntaxFactory.Literal(long.Parse(type.GetText())),
            RavenLexer2.HEX_INTEGER_LITERAL => SyntaxFactory.Literal(long.Parse(type.GetText())),
            RavenLexer2.BIN_INTEGER_LITERAL => SyntaxFactory.Literal(long.Parse(type.GetText())),
            _ => throw new NotSupportedException()
        };
    }

    public override SyntaxNode VisitModifier(RavenParser2.ModifierContext context) {
        var token = (ITerminalNode)context.GetChild(0);

        var kind = token.Symbol.Type switch {
            RavenLexer2.ABSTRACT => SyntaxKind.AbstractKeyword,
            RavenLexer2.CONST => SyntaxKind.ConstKeyword,
            RavenLexer2.OVERRIDE => SyntaxKind.OverrideKeyword,
            RavenLexer2.RECORD => SyntaxKind.RecordKeyword,
            RavenLexer2.PARTIAL => SyntaxKind.PartialKeyword,
            RavenLexer2.PRIVATE => SyntaxKind.PrivateKeyword,
            RavenLexer2.PROTECTED => SyntaxKind.ProtectedKeyword,
            RavenLexer2.PUBLIC => SyntaxKind.PublicKeyword,
            RavenLexer2.READONLY => SyntaxKind.ReadOnlyKeyword,
            RavenLexer2.STATIC => SyntaxKind.StaticKeyword,
            _ => throw new NotSupportedException()
        };

        return Token(token.Symbol, kind);
    }

    /// <summary>
    ///     Builds a red token for the given ANTLR token, carrying its exact source
    ///     text and the leading trivia (whitespace, comments, newlines) that
    ///     immediately precedes it in the token stream.
    /// </summary>
    SyntaxToken Token(IToken source, SyntaxKind kind) {
        // ANTLR's EOF token reports its text as the literal "<EOF>"; the syntax
        // tree models end-of-file as a zero-width marker.
        var text = source.Type == TokenConstants.Eof ? string.Empty : source.Text ?? SyntaxFacts.GetText(kind);
        var leading = GatherLeadingTrivia(source.TokenIndex);
        return (SyntaxToken)new InternalSyntax.SyntaxToken(kind, text, leading).CreateRed(null, 0);
    }

    // A token is "trivia" for the syntax tree if it sits off the default channel
    // (whitespace, comments) or is a newline terminator (which the parser consumes
    // structurally but the tree models as end-of-line trivia).
    // Some tokens (e.g. the '.' in a left-recursive rule) have no generated
    // accessor; find them directly among the context's terminal children.
    static IToken TerminalOf(ParserRuleContext context, int tokenType) =>
        TerminalOrNull(context, tokenType)
        ?? throw new InvalidOperationException($"Expected token {tokenType} in {context.GetType().Name}.");

    static IToken? TerminalOrNull(ParserRuleContext context, int tokenType) {
        for (var i = 0; i < context.ChildCount; i++) {
            if (context.GetChild(i) is ITerminalNode node && node.Symbol.Type == tokenType) {
                return node.Symbol;
            }
        }

        return null;
    }

    // Collects the separator tokens (commas) directly among a rule's terminal
    // children, in source order.
    List<SyntaxToken> Commas(ParserRuleContext context) {
        var commas = new List<SyntaxToken>();
        for (var i = 0; i < context.ChildCount; i++) {
            if (context.GetChild(i) is ITerminalNode node && node.Symbol.Type == RavenLexer2.COMMA) {
                commas.Add(Token(node.Symbol, SyntaxKind.CommaToken));
            }
        }

        return commas;
    }

    // Builds a separated list by interleaving elements with their separators:
    // element, separator, element, separator, element.
    static SeparatedSyntaxList<T> SeparatedList<T>(
        IReadOnlyList<SyntaxNode?> elements,
        IReadOnlyList<SyntaxToken> separators
    )
        where T : SyntaxNode {
        var interleaved = new List<SyntaxNode?>();
        for (var i = 0; i < elements.Count; i++) {
            interleaved.Add(elements[i]);
            if (i < separators.Count) {
                interleaved.Add(separators[i]);
            }
        }

        return new(SyntaxList.List(interleaved.ToArray()));
    }

    static bool IsTrivia(IToken token) =>
        token.Channel != TokenConstants.DefaultChannel || token.Type == RavenLexer2.NL;

    GreenNode? GatherLeadingTrivia(int tokenIndex) {
        if (tokens == null || tokenIndex <= 0) {
            return null;
        }

        var collected = new List<GreenNode>();
        for (var i = tokenIndex - 1; i >= 0; i--) {
            var token = tokens.Get(i);
            if (!IsTrivia(token)) {
                break;
            }

            collected.Add(MapTrivia(token));
        }

        if (collected.Count == 0) {
            return null;
        }

        collected.Reverse();
        return collected.Count == 1 ? collected[0] : InternalSyntax.SyntaxList.List(collected.ToArray());
    }

    static InternalSyntax.SyntaxTrivia MapTrivia(IToken token) {
        var kind = token.Type switch {
            RavenLexer2.NL => SyntaxKind.EndOfLineTrivia,
            RavenLexer2.WHITESPACES => SyntaxKind.WhitespaceTrivia,
            RavenLexer2.SINGLE_LINE_COMMENT or RavenLexer2.SINGLE_LINE_DOC_COMMENT =>
                SyntaxKind.SingleLineCommentTrivia,
            RavenLexer2.DELIMITED_COMMENT
                or RavenLexer2.DELIMITED_DOC_COMMENT
                or RavenLexer2.EMPTY_DELIMITED_DOC_COMMENT =>
                SyntaxKind.MultiLineCommentTrivia,
            _ => SyntaxKind.WhitespaceTrivia
        };

        return new(kind, token.Text);
    }
}
