// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Vixen.Build;

/// <summary>
///     Where a <c>StringId</c> is written in this repository's text, as a function of the text.
/// </summary>
/// <remarks>
///     <para>
///         <b><c>CheckStrings</c> is textual on purpose</b> — a declaration is <c>Class.Member</c> at
///         every site that uses one, in C# and in <c>.vxml</c> alike, and the markup half is why a
///         Roslyn answer would have needed the generated code as well. What that costs is a type
///         name: a construction is only recognisable where the text says what is being built.
///     </para>
///     <para>
///         ⚠ <b>Except the fourth shape, which no text carries at all</b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/1492">#1492</a>). A target-typed
///         <c>new(…)</c> returned, passed or put in a collection says <c>StringId</c> only in a
///         declaration somewhere else, so <see cref="TargetTyped" /> reads C# as a syntax tree —
///         still no compilation and no semantic model, and <c>.vxml</c> stays textual.
///     </para>
///     <para>
///         ⚠ <b>The third shape was invisible for as long as it existed</b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/1203">#1203</a>).
///         <c>Unavailable = new("editor.command.foliage.type-remove.unavailable", "…")</c> inside an
///         object initialiser carries neither a <c>new StringId</c> nor the declared type name the
///         initialiser pattern anchors on — the target's type is only in the semantic model. So the
///         id was in no <c>All</c> list, in no violation, in no count and in no translator's
///         template, which is the exact state the census exists to make impossible.
///     </para>
///     <para>
///         ⚠ <b>The type the text does not carry is recovered from the tree rather than guessed.</b>
///         A naive <c>\w+ = new\(\s*"</c> matches forty-nine object initialisers in this repository
///         and not one of them builds a <c>StringId</c> — <c>StartInfo = new("dotnet")</c>,
///         <c>Endpoint = new("127.0.0.1", 7777)</c>, <c>CasterStage = new("Caster")</c>. What tells
///         them apart is the tree's own declarations: <see cref="Members" /> reads every
///         <c>StringId Foo { get; }</c> and <c>StringId? Foo</c> in the repository, so
///         <c>Unavailable</c> is recognised because <c>EditorCommand</c> declares it as one and
///         <c>StartInfo</c> is not because nothing does. The member set is the semantic model this
///         census can afford.
///     </para>
///     <para>
///         ⚠ <b>Uppercase and property-or-field shaped, which is not style pedantry.</b> An object
///         initialiser assigns to a property or a field; the same regular expression relaxed to any
///         identifier picks up <c>StringId title</c> as a <em>parameter</em> name and then reports
///         <c>this.title = new(title)</c> in <c>BackgroundTask</c> and <c>EditorDocument</c>, where
///         the field is a <c>Signal&lt;string&gt;</c>. Three false positives out of four hits, from
///         one relaxed character class.
///     </para>
/// </remarks>
static class StringIdCensus {
    /// <summary>A <c>StringId</c> property or field declaration, wherever it is written.</summary>
    /// <remarks>
    ///     Both the nullable and the plain form, because <c>EditorCommand.Unavailable</c> has been
    ///     written each way and the census is about the member's name rather than its nullability.
    /// </remarks>
    static readonly Regex MemberPattern = new(
        @"\bStringId\??\s+(?<member>[A-Z]\w*)\s*(?:\{\s*get;|;|=)",
        RegexOptions.Compiled
    );

    /// <summary>Every construction of a <c>StringId</c>, whatever its first argument is.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Four shapes, and each was added after the tree had been living with what the
    ///         previous set could not see.</b> The first is the explicit <c>new StringId("…", …)</c>.
    ///         The second is the initialiser that target-types its <c>new</c> —
    ///         <c>static readonly StringId CategoryWater = new("editor.category.water", "Water");</c> —
    ///         twenty-one production ids written that way, of which <c>editor.category.scene</c> was
    ///         constructed three times in three files while the gate written to stop exactly that
    ///         reported nothing. The third is <see cref="MemberInitialiserConstructionPattern" />
    ///         and the fourth is <see cref="TargetTyped" />.
    ///     </para>
    ///     <para>
    ///         Anchored on the <c>new</c> alone rather than on a literal, and both halves of the
    ///         census read the same sites: whether the first argument is a literal — and so which
    ///         id it is — is read separately at the bracket, by <see cref="LiteralFirstArgument" />
    ///         and <see cref="LiteralIdArgument" />. Two pattern sets that had to agree about where a
    ///         construction is were one more thing that could drift.
    ///     </para>
    /// </remarks>
    static readonly Regex[] ConstructionPatterns = [
        new("""new\s+StringId\(""", RegexOptions.Compiled),
        new("""\bStringId\s+\w+\s*(?:\{\s*get;\s*\}\s*)?=\s*new\(""", RegexOptions.Compiled)
    ];

    /// <summary>The object-initialiser shape, whose target type is only in the member set.</summary>
    /// <remarks>
    ///     Matched against <see cref="Members" /> by the caller — on its own this expression matches
    ///     every object initialiser in the tree, which is why it is not simply a third entry in
    ///     <see cref="ConstructionPatterns" />.
    /// </remarks>
    static readonly Regex MemberInitialiserConstructionPattern = new(
        @"(?<member>\w+)\s*=\s*new\(",
        RegexOptions.Compiled
    );

    /// <summary>A first argument that is a plain string literal and nothing else.</summary>
    static readonly Regex LiteralFirstArgument = new("""^\s*"(?:[^"\\]|\\.)*"\s*,""", RegexOptions.Compiled);

    /// <summary>Every name this repository declares as a <c>StringId</c> property or field.</summary>
    /// <param name="sources">Every source file's text.</param>
    /// <returns>The member names, which is what an object initialiser assigns to.</returns>
    /// <remarks>
    ///     ⚠ <b>Tree-wide rather than per file, and that is the whole of why it works.</b>
    ///     <c>FoliageMode.cs</c> assigns <c>Unavailable</c>; the declaration that says
    ///     <c>Unavailable</c> is a <c>StringId</c> is in <c>Vixen.Ui.Controls/EditorCommand.cs</c>,
    ///     one assembly away. A per-file answer would have missed the id this shape was added for.
    /// </remarks>
    public static HashSet<string> Members(IEnumerable<string> sources) {
        ArgumentNullException.ThrowIfNull(sources);

        var members = new HashSet<string>(StringComparer.Ordinal);

        foreach (var contents in sources) {
            foreach (Match match in MemberPattern.Matches(contents)) {
                members.Add(match.Groups["member"].Value);
            }
        }

        return members;
    }

    /// <summary>
    ///     Every method, constructor and delegate name in the tree that takes a <c>StringId</c>, by
    ///     where the parameter is and what it is called.
    /// </summary>
    /// <param name="sources">Every source file's text.</param>
    /// <returns>The set an argument written as a target-typed <c>new(…)</c> is resolved against.</returns>
    /// <remarks>
    ///     ⚠ <b>The argument shape's target is in another file's parameter list</b>, the way the
    ///     object initialiser's is in another file's property — <c>Verb(id, new("editor.command." +
    ///     id, text), …)</c> says nothing about <c>StringId</c> itself. So, like
    ///     <see cref="Members" />, this is gathered over the whole tree, and an overload the tree
    ///     does not declare (a BCL <c>List&lt;T&gt;.Add</c>) cannot make a name count.
    /// </remarks>
    public static StringIdParameters Parameters(IEnumerable<string> sources) {
        ArgumentNullException.ThrowIfNull(sources);

        var parameters = new StringIdParameters();

        foreach (var contents in sources) {
            if (!contents.Contains("StringId", StringComparison.Ordinal)) {
                continue;
            }

            foreach (var node in Parse(contents).GetRoot().DescendantNodes()) {
                var (name, list) = node switch {
                    MethodDeclarationSyntax method => (method.Identifier.ValueText, method.ParameterList),
                    LocalFunctionStatementSyntax local => (local.Identifier.ValueText, local.ParameterList),
                    ConstructorDeclarationSyntax constructor => (constructor.Identifier.ValueText,
                        constructor.ParameterList),
                    DelegateDeclarationSyntax @delegate => (@delegate.Identifier.ValueText, @delegate.ParameterList),
                    TypeDeclarationSyntax { ParameterList: { } primary } type => (type.Identifier.ValueText, primary),
                    _ => (null, null)
                };

                if (name is null || list is null) {
                    continue;
                }

                // An extension method is called with its receiver outside the argument list, so
                // its second parameter is the call's first argument.
                var shift = list.Parameters.FirstOrDefault()?.Modifiers.Any(SyntaxKind.ThisKeyword) == true ? 1 : 0;

                for (var position = 0; position < list.Parameters.Count; position++) {
                    var parameter = list.Parameters[position];

                    if (IsStringId(parameter.Type) && !parameter.Modifiers.Any(SyntaxKind.ParamsKeyword)) {
                        parameters.Add(name, position - shift, parameter.Identifier.ValueText);
                    }
                }
            }
        }

        return parameters;
    }

    /// <summary>Every id one file builds out of a string literal, under any of the four shapes.</summary>
    /// <param name="contents">The file.</param>
    /// <param name="members">Every name declared as a <c>StringId</c>, from <see cref="Members" />.</param>
    /// <param name="parameters">
    ///     Every <c>StringId</c> parameter, from <see cref="Parameters" />; without it a target-typed
    ///     <c>new(…)</c> passed as an argument is not seen.
    /// </param>
    /// <returns>The id and the offset the match started at, once per construction.</returns>
    /// <remarks>
    ///     ⚠ <b>Deduplicated on where the argument list starts</b>, because a declaration written as
    ///     <c>static readonly StringId CategoryWater = new("…", "…")</c> satisfies the second shape,
    ///     the third and the fourth at once. Several shapes agreeing about one site is one site.
    /// </remarks>
    public static IEnumerable<(string Id, int Index)> LiteralIds(
        string contents,
        IReadOnlySet<string> members,
        StringIdParameters? parameters = null
    ) {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(members);

        foreach (var (index, arguments) in Sites(contents, members, parameters)) {
            if (LiteralIdArgument.Match(contents, arguments) is { Success: true } literal) {
                yield return (literal.Groups["id"].Value, index);
            }
        }
    }

    /// <summary>Every construction of a <c>StringId</c> in one file, literal id or not.</summary>
    /// <param name="contents">The file.</param>
    /// <param name="members">Every name declared as a <c>StringId</c>, from <see cref="Members" />.</param>
    /// <param name="parameters">
    ///     Every <c>StringId</c> parameter, from <see cref="Parameters" />; without it a target-typed
    ///     <c>new(…)</c> passed as an argument is not seen.
    /// </param>
    /// <returns>Where the match started, and whether its first argument is a plain literal.</returns>
    public static IEnumerable<(int Index, bool Literal)> Constructions(
        string contents,
        IReadOnlySet<string> members,
        StringIdParameters? parameters = null
    ) {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(members);

        return Sites(contents, members, parameters)
            .Select(site => (site.Index, LiteralFirstArgument.IsMatch(contents[site.Arguments..])));
    }

    /// <summary>Whether an offset falls on a <c>///</c> line.</summary>
    /// <param name="contents">The file.</param>
    /// <param name="index">Where the match started.</param>
    /// <returns>Whether the line it is on is a documentation comment.</returns>
    /// <remarks>
    ///     ⚠ <b>A <c>///</c> line is not a call site.</b> Three of the ids the census saw on the day
    ///     it was written were prose, and every one of the object-initialiser shape's hits on this
    ///     tree today is a remark explaining the shape — including this file's own. Nothing drifts
    ///     when prose and a declaration disagree: a reader sees both.
    /// </remarks>
    public static bool InDocComment(string contents, int index) {
        ArgumentNullException.ThrowIfNull(contents);

        var start = contents.LastIndexOf('\n', Math.Max(index - 1, 0)) + 1;

        return contents.AsSpan(start, index - start).TrimStart().StartsWith("///", StringComparison.Ordinal);
    }

    /// <summary>A string literal where the argument list starts, read at a known offset.</summary>
    static readonly Regex LiteralIdArgument = new("""\G\s*"(?<id>[^"]+)"\s*,""", RegexOptions.Compiled);

    /// <summary>The trees already parsed, keyed on the text itself.</summary>
    /// <remarks>
    ///     <c>CheckStrings</c> asks about every file three times — once for <see cref="Parameters" />
    ///     and once each for the literal and construction halves — and parsing is the whole of the
    ///     fourth shape's cost.
    /// </remarks>
    static readonly ConditionalWeakTable<string, SyntaxTree> Trees = new();

    static SyntaxTree Parse(string contents) =>
        Trees.GetValue(
            contents,
            text => CSharpSyntaxTree.ParseText(
                text,
                CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview)
            )
        );

    /// <summary>The union of every shape, one entry per site.</summary>
    /// <param name="contents">The file.</param>
    /// <param name="members">Every name declared as a <c>StringId</c>.</param>
    /// <param name="parameters">Every <c>StringId</c> parameter, or null to leave arguments unseen.</param>
    /// <returns>Where each site starts and where its argument list starts, deduplicated on the second.</returns>
    static IEnumerable<(int Index, int Arguments)> Sites(
        string contents,
        IReadOnlySet<string> members,
        StringIdParameters? parameters
    ) {
        var seen = new HashSet<int>();
        var textual = ConstructionPatterns
            .SelectMany(pattern => pattern.Matches(contents))
            .Concat(
                MemberInitialiserConstructionPattern
                    .Matches(contents)
                    .Where(match => members.Contains(match.Groups["member"].Value))
            )
            .Select(match => (match.Index, Arguments: ArgumentsAfter(contents, match)))
            .Where(site => site.Arguments >= 0);

        foreach (var site in textual.Concat(TargetTyped(contents, parameters))) {
            if (seen.Add(site.Arguments)) {
                yield return site;
            }
        }
    }

    /// <summary>The offset just past the <c>new(</c> a textual pattern matched, which every pattern contains.</summary>
    static int ArgumentsAfter(string contents, Match match) {
        var open = contents.IndexOf('(', match.Value.LastIndexOf("new", StringComparison.Ordinal) + match.Index);

        return open < 0 ? -1 : open + 1;
    }

    /// <summary>
    ///     The fourth shape: a target-typed <c>new(…)</c> whose target is a <c>StringId</c> only
    ///     because of where it is written.
    /// </summary>
    /// <param name="contents">The file.</param>
    /// <param name="parameters">Every <c>StringId</c> parameter, or null to leave arguments unseen.</param>
    /// <returns>Each site and the offset just past its opening bracket.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Invisible to every textual pattern, and a shipping call site used it to pass the
    ///         gate</b> (<a href="https://github.com/Rikarin/Vixen/issues/1492">#1492</a>,
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1270">#1270</a>).
    ///         <c>static StringId Label(string id, string text) => new("editor.command." + id, text);</c>
    ///         carries no <c>new StringId</c>, no <c>StringId x =</c> and no member assignment, so it
    ///         was in no count; written <c>new StringId(…)</c> the same line counts. Dropping a type
    ///         name was enough to leave the census.
    ///     </para>
    ///     <para>
    ///         <b>A syntax tree rather than a pattern, and still no semantic model.</b> The target is
    ///         read from the declaration the expression sits in: a variable, field or property typed
    ///         <c>StringId</c>; the return type of the method, local function, property, indexer or
    ///         accessor a <c>return</c> or an <c>=&gt;</c> belongs to; the element type of an array,
    ///         a one-argument generic or a collection expression; and, for an argument, the tree's
    ///         own parameter lists (<see cref="Parameters" />). A conditional, a <c>??</c> and a
    ///         switch arm pass the target through. ⚠ What it still cannot see, said rather than
    ///         implied: a lambda's return (its type is the delegate's, which is a semantic question),
    ///         a dictionary's value, an argument to a target-typed <c>new(…)</c> of something else,
    ///         and a <c>.vxml</c> code block, which is not parsed — the textual shapes still read
    ///         those.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An argument is resolved by name, so it counts only when it looks like an id</b>:
    ///         two arguments, the first a string literal, an interpolation or a concatenation that
    ///         starts with one. Without that, the first run on the tree reported forty-seven sites
    ///         that build a loot table or a node position for a <c>Dictionary</c>'s <c>Add</c> or a
    ///         graph's, because some <c>Add</c> in the tree takes a <c>StringId</c> second. The cost
    ///         is <c>Verb(id, new(id, text))</c> with no literal in it, which stays unseen.
    ///     </para>
    /// </remarks>
    static IEnumerable<(int Index, int Arguments)> TargetTyped(string contents, StringIdParameters? parameters) {
        if (!contents.Contains("new(", StringComparison.Ordinal)) {
            yield break;
        }

        foreach (var creation in Parse(contents).GetRoot().DescendantNodes().OfType<ImplicitObjectCreationExpressionSyntax>()) {
            if (IsStringIdTarget(creation, parameters)) {
                yield return (creation.SpanStart, creation.ArgumentList.OpenParenToken.Span.End);
            }
        }
    }

    /// <summary>Whether the expression is written where a <c>StringId</c> is expected.</summary>
    static bool IsStringIdTarget(ExpressionSyntax expression, StringIdParameters? parameters) {
        var node = Outermost(expression);

        if (IsStringId(DeclaredType(node))) {
            return true;
        }

        switch (node.Parent) {
            // ⚠ By name, so only where the construction itself looks like an id: two arguments,
            // the first a string written out. Resolved by name alone, the first run of this shape
            // on the tree reported forty-seven `Add(key, new(definition, …))` and
            // `Graph.Add("…", new(80f, 80f))` sites — a Dictionary's Add and a node graph's, which
            // share a name with some Add in the tree that takes a StringId.
            case ArgumentSyntax argument when parameters is not null:
                return expression is ImplicitObjectCreationExpressionSyntax { ArgumentList.Arguments: [var id, _] }
                    && IsWrittenString(id.Expression)
                    && parameters.Takes(argument);

            case ExpressionElementSyntax { Parent: CollectionExpressionSyntax collection }:
                return IsStringId(ElementType(DeclaredType(Outermost(collection))));

            case InitializerExpressionSyntax initializer when initializer.IsKind(SyntaxKind.ArrayInitializerExpression)
                || initializer.IsKind(SyntaxKind.CollectionInitializerExpression):
                return IsStringId(
                    ElementType(
                        initializer.Parent switch {
                            ArrayCreationExpressionSyntax array => array.Type,
                            ObjectCreationExpressionSyntax created => created.Type,
                            EqualsValueClauseSyntax => DeclaredType(initializer),
                            ImplicitObjectCreationExpressionSyntax created => DeclaredType(Outermost(created)),
                            _ => null
                        }
                    )
                );

            default:
                return false;
        }
    }

    /// <summary>A string literal, an interpolated string, or a concatenation that starts with one.</summary>
    static bool IsWrittenString(ExpressionSyntax expression) =>
        expression switch {
            LiteralExpressionSyntax literal => literal.IsKind(SyntaxKind.StringLiteralExpression),
            InterpolatedStringExpressionSyntax => true,
            BinaryExpressionSyntax concatenation when concatenation.IsKind(SyntaxKind.AddExpression) =>
                IsWrittenString(concatenation.Left),
            ParenthesizedExpressionSyntax parenthesised => IsWrittenString(parenthesised.Expression),
            _ => false
        };

    /// <summary>The expression a target passes through unchanged, climbed to the one that receives it.</summary>
    static ExpressionSyntax Outermost(ExpressionSyntax expression) {
        while (true) {
            switch (expression.Parent) {
                case ParenthesizedExpressionSyntax parenthesised:
                    expression = parenthesised;
                    continue;
                case ConditionalExpressionSyntax conditional when conditional.Condition != expression:
                    expression = conditional;
                    continue;
                case BinaryExpressionSyntax coalesce when coalesce.IsKind(SyntaxKind.CoalesceExpression):
                    expression = coalesce;
                    continue;
                case SwitchExpressionArmSyntax { Parent: SwitchExpressionSyntax switched } arm
                    when arm.Expression == expression:
                    expression = switched;
                    continue;
                default:
                    return expression;
            }
        }
    }

    /// <summary>The type the declaration an expression is the value of says it has.</summary>
    static TypeSyntax? DeclaredType(ExpressionSyntax expression) =>
        expression.Parent switch {
            EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } } =>
                declaration.Type,
            EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax property } => property.Type,
            ArrowExpressionClauseSyntax arrow => ReturnType(arrow.Parent),
            ReturnStatementSyntax @return => ReturnType(
                @return.Ancestors()
                    .FirstOrDefault(ancestor => ancestor is BaseMethodDeclarationSyntax
                        or LocalFunctionStatementSyntax
                        or AccessorDeclarationSyntax
                        or AnonymousFunctionExpressionSyntax)
            ),
            _ => null
        };

    /// <summary>What a member returns, or null where the syntax alone cannot say.</summary>
    static TypeSyntax? ReturnType(SyntaxNode? member) =>
        member switch {
            MethodDeclarationSyntax method => method.ReturnType,
            LocalFunctionStatementSyntax local => local.ReturnType,
            OperatorDeclarationSyntax @operator => @operator.ReturnType,
            ConversionOperatorDeclarationSyntax conversion => conversion.Type,
            BasePropertyDeclarationSyntax property => property.Type,
            AccessorDeclarationSyntax { Parent.Parent: BasePropertyDeclarationSyntax property } accessor
                when accessor.IsKind(SyntaxKind.GetAccessorDeclaration) => property.Type,
            _ => null
        };

    /// <summary>The element type of an array or a one-argument generic collection.</summary>
    static TypeSyntax? ElementType(TypeSyntax? type) =>
        type switch {
            ArrayTypeSyntax array => array.ElementType,
            GenericNameSyntax { TypeArgumentList.Arguments.Count: 1 } generic => generic.TypeArgumentList.Arguments[0],
            QualifiedNameSyntax qualified => ElementType(qualified.Right),
            NullableTypeSyntax nullable => ElementType(nullable.ElementType),
            _ => null
        };

    /// <summary>Whether a type is written as <c>StringId</c>, qualified or nullable.</summary>
    internal static bool IsStringId(TypeSyntax? type) =>
        type switch {
            IdentifierNameSyntax { Identifier.ValueText: "StringId" } => true,
            QualifiedNameSyntax qualified => IsStringId(qualified.Right),
            AliasQualifiedNameSyntax alias => IsStringId(alias.Name),
            NullableTypeSyntax nullable => IsStringId(nullable.ElementType),
            _ => false
        };
}

/// <summary>Every <c>StringId</c> parameter the tree declares, by the name a call site uses.</summary>
/// <remarks>
///     By name rather than by overload, because a call site's overload is a semantic question: a
///     position counts when <em>any</em> declaration of that name takes a <c>StringId</c> there.
///     That is louder than the compiler, never quieter — the direction a census can afford.
/// </remarks>
sealed class StringIdParameters {
    readonly Dictionary<string, HashSet<int>> Positions = new(StringComparer.Ordinal);
    readonly Dictionary<string, HashSet<string>> Names = new(StringComparer.Ordinal);

    /// <summary>How many callable names take a <c>StringId</c> somewhere.</summary>
    public int Count => Positions.Count;

    /// <summary>Records one parameter.</summary>
    /// <param name="callable">The method, constructor, type or delegate name.</param>
    /// <param name="position">Where it is in a call's argument list.</param>
    /// <param name="parameter">Its name, for a named argument.</param>
    public void Add(string callable, int position, string parameter) {
        if (position >= 0) {
            if (!Positions.TryGetValue(callable, out var at)) {
                Positions[callable] = at = [];
            }

            at.Add(position);
        }

        if (!Names.TryGetValue(callable, out var named)) {
            Names[callable] = named = [];
        }

        named.Add(parameter);
    }

    /// <summary>Whether some declaration of a name takes a <c>StringId</c> at a position.</summary>
    /// <param name="callable">The method, constructor, type or delegate name.</param>
    /// <param name="position">Where in a call's argument list.</param>
    /// <returns>Whether one does.</returns>
    public bool Takes(string callable, int position) =>
        Positions.TryGetValue(callable, out var at) && at.Contains(position);

    /// <summary>Whether an argument is in a position some declaration of its callee types as <c>StringId</c>.</summary>
    /// <param name="argument">The argument.</param>
    /// <returns>Whether it is.</returns>
    public bool Takes(ArgumentSyntax argument) {
        if (argument.Parent is not BaseArgumentListSyntax list) {
            return false;
        }

        var callee = list.Parent switch {
            InvocationExpressionSyntax { Expression: var expression } => Name(expression),
            ObjectCreationExpressionSyntax created => Name(created.Type),
            PrimaryConstructorBaseTypeSyntax primary => Name(primary.Type),
            ConstructorInitializerSyntax initializer when initializer.IsKind(SyntaxKind.ThisConstructorInitializer) =>
                initializer.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText,
            _ => null
        };

        if (callee is null) {
            return false;
        }

        if (argument.NameColon is { } named) {
            return Names.TryGetValue(callee, out var set) && set.Contains(named.Name.Identifier.ValueText);
        }

        return Positions.TryGetValue(callee, out var at) && at.Contains(list.Arguments.IndexOf(argument));
    }

    static string? Name(SyntaxNode node) =>
        node switch {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            MemberAccessExpressionSyntax access => Name(access.Name),
            MemberBindingExpressionSyntax binding => Name(binding.Name),
            QualifiedNameSyntax qualified => Name(qualified.Right),
            AliasQualifiedNameSyntax alias => Name(alias.Name),
            _ => null
        };
}
