// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vixen.Ui.Generators;

/// <summary>Checks a <c>StringId</c> declaration class against the two sides nobody was comparing.</summary>
/// <remarks>
///     <para>
///         <b>A declaration class writes every string twice</b> — once as a property, once as a name
///         in the <c>All</c> list beside it — and until this analyzer existed nothing compared the
///         two. A declaration left out of <c>All</c> is in no translator's template and is therefore
///         permanently English, and the omission is invisible: the class compiles, the label shows,
///         and the only symptom is a language that is quietly incomplete.
///     </para>
///     <para>
///         ⚠ <b>It checks the shape rather than deciding it.</b> <c>docs/plan/46</c> § A3 is explicit
///         that the declaration shape is the thing worth having upstream, because a second generator
///         outside this repository emits the same shape and a string moved between the two should be
///         a copy rather than a translation. So nothing here asks a declaration class to be written
///         differently: an id, its source text, and an <c>All</c> list is exactly what
///         <c>EditorStrings</c> and <c>ControlStrings</c> already are, and what a generated class
///         emits. What this adds is that the shape is now enforced instead of remembered — which is
///         also what makes an independently written generator's output verifiable here.
///     </para>
///     <para>
///         ⚠ <b>It is an analyzer rather than a generator, and that is the finding rather than a
///         shortcut.</b> A generator would need catalogue *source* — a file the ids come from — and
///         where that file lives is undecided for Vixen and is Trinix's by its own scope rule. Every
///         property doc 11 asks a generator for that does not need that decision is decidable by
///         reading the declarations, and this is that half. The other half — an id declared and used
///         nowhere at all — cannot be seen from inside one compilation, because six of
///         <c>ControlStrings</c>' thirteen are used only from <c>Vixen.Ui.Controls.Advanced</c>; that
///         is <c>./build.sh CheckStrings</c>, which reads the whole tree.
///     </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StringDeclarationAnalyzer : DiagnosticAnalyzer {
    /// <summary>The id reported for a declaration missing from its <c>All</c> list.</summary>
    public const string MissingFromAllId = "VXS0310";

    /// <summary>The id reported for two declarations sharing one string id.</summary>
    public const string DuplicateIdId = "VXS0311";

    /// <summary>The id reported for a <c>StringId</c> built outside a declaration class.</summary>
    public const string UndeclaredId = "VXS0312";

    const string StringIdMetadataName = "Vixen.Ui.StringId";
    const string AllPropertyName = "All";

    static readonly DiagnosticDescriptor MissingFromAll = new(
        MissingFromAllId,
        "A declared string is not in the All list",
        "'{0}.{1}' is declared but is not in '{0}.All', so no translator's template contains it",
        "Vixen.Ui",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        "All is what Strings.Template exports and therefore the whole of what a translator ever sees. "
        + "It is spelled out rather than reflected over — a list gathered at run time is one a trimmer "
        + "is entitled to shorten — so the name is written twice and this is what compares the two."
    );

    static readonly DiagnosticDescriptor DuplicateId = new(
        DuplicateIdId,
        "Two declarations share one string id",
        "'{0}' is declared here and at {1}; a catalogue is a map, so the second translation wins and the first string is unreachable",
        "Vixen.Ui",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        "An id is what a catalogue calls a string. Two declarations under one id are two places a "
        + "translator cannot tell apart and one of which can never be translated separately — which is "
        + "the exact thing the two ids for the two \"Close\"s exist to avoid.",
        customTags: [WellKnownDiagnosticTags.CompilationEnd]
    );

    static readonly DiagnosticDescriptor Undeclared = new(
        UndeclaredId,
        "A string id is declared nowhere",
        "This string id is built here rather than declared in '{0}', so it is in no All list and no translator's template contains it",
        "Vixen.Ui",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        "Reported only in an assembly that already has a declaration class, because that assembly has "
        + "answered the question of where its ids live and a second answer at a call site is a string "
        + "that silently cannot be translated.",
        customTags: [WellKnownDiagnosticTags.CompilationEnd]
    );

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [MissingFromAll, DuplicateId, Undeclared];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context) {
        // Generated code is neither analyzed nor reported against: a `.vxml` may declare a StringId
        // in its code block, and the file an author would have to edit is the markup rather than the
        // C# a diagnostic would point at.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(
            start => {
                var stringId = start.Compilation.GetTypeByMetadataName(StringIdMetadataName);

                if (stringId is null) {
                    return;
                }

                // ⚠ Gathered here and reported at compilation end, because two of the three rules are
                // about the compilation rather than about a file: whether an id is declared twice,
                // and whether this assembly has a declaration class at all. A per-file action can
                // answer neither, and answering them from a syntax action would make the report
                // depend on which file the compiler reached first.
                var classes = new ConcurrentDictionary<string, byte>();
                var declarations = new ConcurrentBag<Declaration>();
                var loose = new ConcurrentBag<Location>();

                start.RegisterSyntaxNodeAction(
                    node => Collect(node, stringId, classes, declarations),
                    SyntaxKind.ClassDeclaration
                );

                start.RegisterSyntaxNodeAction(
                    node => Creation(node, stringId, loose),
                    SyntaxKind.ObjectCreationExpression,
                    SyntaxKind.ImplicitObjectCreationExpression
                );

                start.RegisterCompilationEndAction(end => Report(end, classes, declarations, loose));
            }
        );
    }

    static void Collect(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol stringId,
        ConcurrentDictionary<string, byte> classes,
        ConcurrentBag<Declaration> declarations
    ) {
        var syntax = (ClassDeclarationSyntax)context.Node;

        if (context.SemanticModel.GetDeclaredSymbol(syntax, context.CancellationToken) is not { } type) {
            return;
        }

        var members = syntax.Members.OfType<PropertyDeclarationSyntax>().ToArray();
        var all = members.FirstOrDefault(member => member.Identifier.ValueText == AllPropertyName);

        var declared = members
            .Where(member => member.Initializer is not null)
            .Where(member => IsStringId(context.SemanticModel, member, stringId, context.CancellationToken))
            .ToArray();

        if (all is null || declared.Length == 0) {
            // Not a declaration class. A type that happens to hold one StringId — a mode's own title,
            // a fixture — is not making a claim about where an assembly's ids live, and All is what
            // distinguishes the two.
            return;
        }

        classes[type.ToDisplayString()] = 0;

        var listed = all.Initializer is null
            ? new HashSet<string>()
            : new HashSet<string>(
                all.Initializer.DescendantNodes()
                    .OfType<IdentifierNameSyntax>()
                    .Select(identifier => identifier.Identifier.ValueText)
            );

        foreach (var member in declared) {
            declarations.Add(
                new Declaration(
                    type.Name,
                    member.Identifier.ValueText,
                    IdOf(member.Initializer!.Value),
                    member.Identifier.GetLocation()
                )
            );

            // ⚠ Reported from here rather than at compilation end, unlike the other two. This one is
            // decidable from the class alone, and a diagnostic that needs no compilation end is one
            // an editor shows while the omission is being typed rather than at the next full build.
            if (!listed.Contains(member.Identifier.ValueText)) {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        MissingFromAll,
                        member.Identifier.GetLocation(),
                        type.Name,
                        member.Identifier.ValueText
                    )
                );
            }
        }
    }

    static void Creation(SyntaxNodeAnalysisContext context, INamedTypeSymbol stringId, ConcurrentBag<Location> loose) {
        var created = context.SemanticModel.GetTypeInfo(context.Node, context.CancellationToken).Type;

        if (created is null || !SymbolEqualityComparer.Default.Equals(created, stringId)) {
            return;
        }

        // A declaration class's own members are where a StringId is supposed to be built; anything
        // else in an assembly that has one is a second, invisible place.
        if (context.Node.FirstAncestorOrSelf<ClassDeclarationSyntax>() is { } within
            && within.Members.OfType<PropertyDeclarationSyntax>()
                .Any(member => member.Identifier.ValueText == AllPropertyName)) {
            return;
        }

        loose.Add(context.Node.GetLocation());
    }

    static void Report(
        CompilationAnalysisContext context,
        ConcurrentDictionary<string, byte> classes,
        ConcurrentBag<Declaration> declarations,
        ConcurrentBag<Location> loose
    ) {
        // Ordered, so which of a pair is named as "the other one" does not depend on the order the
        // compiler happened to walk the files in — a diagnostic that moves between builds is one
        // nobody can baseline.
        foreach (var group in declarations
                     .Where(declaration => declaration.Id is not null)
                     .GroupBy(declaration => declaration.Id!, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1)) {
            var ordered = group.OrderBy(declaration => declaration.Member, StringComparer.Ordinal).ToArray();

            for (var index = 1; index < ordered.Length; index++) {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        DuplicateId,
                        ordered[index].Location,
                        group.Key,
                        Describe(ordered[0])
                    )
                );
            }
        }

        if (classes.IsEmpty) {
            return;
        }

        var named = classes.Keys.OrderBy(name => name, StringComparer.Ordinal).First();

        foreach (var location in loose) {
            context.ReportDiagnostic(Diagnostic.Create(Undeclared, location, named));
        }
    }

    static string Describe(Declaration declaration) =>
        declaration.TypeName + "." + declaration.Member;

    static bool IsStringId(
        SemanticModel model,
        PropertyDeclarationSyntax member,
        INamedTypeSymbol stringId,
        CancellationToken cancellation
    ) =>
        model.GetDeclaredSymbol(member, cancellation) is { } symbol
        && SymbolEqualityComparer.Default.Equals(symbol.Type, stringId);

    /// <summary>The id a declaration's initializer names, when it is a literal.</summary>
    /// <remarks>
    ///     A computed id — <c>"editor.command." + name</c> — reads as null and is not compared. Those
    ///     exist and are legitimate: an editor mode registers a command per tool, and the id is built
    ///     from the tool's. What cannot be compared is not reported on.
    /// </remarks>
    static string? IdOf(ExpressionSyntax initializer) {
        var arguments = initializer switch {
            ObjectCreationExpressionSyntax creation => creation.ArgumentList?.Arguments,
            ImplicitObjectCreationExpressionSyntax implicitCreation => implicitCreation.ArgumentList.Arguments,
            _ => null
        };

        if (arguments is not { Count: > 0 }) {
            return null;
        }

        return arguments.Value[0].Expression is LiteralExpressionSyntax literal
            && literal.IsKind(SyntaxKind.StringLiteralExpression)
                ? literal.Token.ValueText
                : null;
    }

    sealed record Declaration(string TypeName, string Member, string? Id, Location Location);
}
