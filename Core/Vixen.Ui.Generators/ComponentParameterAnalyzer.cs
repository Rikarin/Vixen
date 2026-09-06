// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Vixen.Ui.Generators;

/// <summary>Says which of a component's public properties can follow the value bound to them.</summary>
/// <remarks>
///     <para>
///         <b>A component's public settable properties are its parameter surface</b> —
///         <c>&lt;Panel Model="@Model" /&gt;</c> assigns one — and whether a parameter <i>tracks</i>
///         its source is decided entirely by how the property is declared. An effect subscribes to
///         what it reads; a plain auto-property is not something to subscribe to, so a binding
///         assigns it, the child reads it once while building, and every later change to the
///         caller's expression reaches nothing.
///     </para>
///     <para>
///         ⚠ <b>This is what is left of the defect after the ordering half was fixed.</b> The markup
///         emitter now writes <c>Create</c> … assignments … <c>Compose</c>, so the value a child is
///         <i>built</i> with is the caller's rather than the property's default. That was the loud
///         half. What it cannot fix is tracking, because signal-backing is what makes a property
///         something an effect can subscribe to and no arrangement of statements substitutes for it.
///     </para>
///     <para>
///         ⚠ <b>It cannot be a <c>VXML2xxx</c>, and the issue that proposed one called that the
///         cheaper option.</b> <c>VxmlGenerator</c> never takes a <c>Compilation</c> — deliberately,
///         so that editing a C# file re-runs no markup — and <c>Binder</c> is pure syntax, so "is
///         this property signal-backed" is a question the markup compiler cannot ask at all. It has
///         to be a separate analyzer over the C#, which is this.
///     </para>
///     <para>
///         ⚠ <b>Reported as a suggestion rather than a warning, and the reason is a rule of this
///         repository rather than a doubt about the diagnostic.</b> <c>TreatWarningsAsErrors</c> is a
///         stated non-negotiable, so a warning that fires anywhere in the tree is a broken build —
///         and a plain parameter is sometimes right: a label a caller sets once and never changes
///         does not need a signal and would be paying for one. Promoting this to a warning is a
///         decision to take after the tree has been swept with it, not before.
///     </para>
///     <para>
///         ⚠ <b>A full <c>Compile</c> does not sweep it, and expecting one to is the trap.</b>
///         Analyzers do not flow transitively through a <c>ProjectReference</c> —
///         <c>Vixen.Ui.csproj</c> says so three lines above the reference that loads this — so the
///         rule runs only where a project names <c>Vixen.Ui.Generators</c> itself or sets
///         <c>&lt;VixenUi&gt;true&lt;/VixenUi&gt;</c>. Six <c>.vxml</c>-owning projects in this
///         repository name only <c>Vixen.Ui.Markup.Generators</c>, which is what compiles the
///         markup, and therefore never see this at all. A consumer outside the repository has no
///         such gap: a <c>PackageReference</c> to <c>Vixen.Ui</c> carries both.
///     </para>
///     <para>
///         ⚠ <b>And while it is <see cref="DiagnosticSeverity.Info" /> it prints nothing at any
///         MSBuild verbosity</b>, so a sweep that greps a build log for the id reads zero whether or
///         not there is anything to find. Promote it in <c>.editorconfig</c> for the run —
///         <c>TreatWarningsAsErrors</c> then turns every hit into a build error that cannot be
///         missed — and put the severity back afterwards.
///     </para>
///     <para>
///         ⚠ <b>A parameter declared in a <c>.vxml</c>'s <c>@code</c> block is reached through the
///         <c>#line</c>, and that is the only reason generated code is analyzed at all.</b> The
///         block is copied into a file whose first line is <c>// &lt;auto-generated /&gt;</c>, so
///         under <see cref="GeneratedCodeAnalysisFlags.None" /> the declaration was neither analyzed
///         nor reported — and that is where every markup component in this repository puts its
///         parameters, <c>Samples/02-HelloUi/Panels/Inspector.vxml</c>'s <c>Model</c> included. The
///         objection to reporting in generated code is that it points at code the author cannot
///         edit; <c>ComponentEmitter</c> writes each block under a <c>#line</c> span, which answers
///         exactly that objection, so <see cref="Reported" /> reports against the <c>.vxml</c> when
///         a mapping exists. Where none does, the location stays inside the generated tree and
///         Roslyn drops it, because
///         <see cref="GeneratedCodeAnalysisFlags.ReportDiagnostics" /> is deliberately not set.
///     </para>
///     <para>
///         ⚠ <b>Reactive is a question about the accessors and not only about the type, and asking
///         only the narrow half made the rule fire on the shape its own message asks for.</b>
///         <c>public ShellModel Model { get =&gt; model.Value; set =&gt; model.Value = value; }</c>
///         over a <c>readonly Signal&lt;ShellModel&gt;</c> is precisely "back it with a
///         <c>Signal&lt;T&gt;</c>", and it <i>does</i> track — an effect reading it reads
///         <c>model.Value</c> and subscribes — yet <see cref="IsReactive" /> tests the property's
///         type, which is <c>ShellModel</c>. The operation-block half of <see cref="Inspect" /> is
///         the other: a getter that reads anything reactive is subscribable whatever the property's
///         own type says, and an auto-property — the case the rule exists for — has no operation
///         block to look at.
///     </para>
///     <para>
///         <b>What is deliberately not reported.</b> A read-only or computed property is not a
///         parameter — a caller cannot assign it — and neither is a delegate: a callback parameter is
///         invoked rather than read, so nothing about it needs to be subscribable. A property whose
///         type is already reactive is the shape this rule is asking for.
///     </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ComponentParameterAnalyzer : DiagnosticAnalyzer {
    /// <summary>The id reported for a component parameter that cannot follow its source.</summary>
    public const string PlainParameterId = "VXS0320";

    const string ComponentMetadataName = "Vixen.Ui.Composition.Component";
    const string SignalNamespace = "Vixen.Ui.Reactive";
    const string ReadOnlySignalMetadataName = "Vixen.Ui.Reactive.IReadOnlySignal`1";

    static readonly DiagnosticDescriptor PlainParameter = new(
        PlainParameterId,
        "A component parameter cannot follow its source",
        "'{0}.{1}' is a component parameter that nothing can subscribe to, so a binding assigns it "
        + "once and later changes to the caller's expression never reach it. Back it with a "
        + "Signal<{2}> if it is meant to keep up.",
        "Vixen.Ui",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        "Markup assigns a component's public properties as parameters and then keeps them current "
        + "with an effect. An effect subscribes to what it reads and a plain property is not "
        + "something to subscribe to, so the value survives exactly as long as nobody changes it. "
        + "The markup compiler cannot report this — VxmlGenerator takes no Compilation on purpose — "
        + "which is why it is here."
    );

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [PlainParameter];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context) {
        // ⚠ Analyzed but not reported against, and the asymmetry is the whole point. `Analyze` is
        // what lets a `.vxml`'s `@code` block be seen at all — it is compiled into a file marked
        // `// <auto-generated />`, and that is where every markup component in this repository
        // declares its parameters. Withholding `ReportDiagnostics` is what keeps the old promise:
        // a location still inside the generated tree is dropped by Roslyn, so the only diagnostic
        // that survives is one `Reported` moved onto the `.vxml` through the emitter's `#line`,
        // which is a place the author can act.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(
            start => {
                var component = start.Compilation.GetTypeByMetadataName(ComponentMetadataName);

                if (component is null) {
                    return;
                }

                var readOnlySignal = start.Compilation.GetTypeByMetadataName(ReadOnlySignalMetadataName);

                start.RegisterSymbolStartAction(
                    symbol => Inspect(symbol, component, readOnlySignal),
                    SymbolKind.NamedType
                );
            }
        );
    }

    /// <summary>Collects a type's plain parameters, then lets its getters argue them away.</summary>
    /// <remarks>
    ///     ⚠ <b>Two passes rather than one, because "what does the getter read" is a question about
    ///     an operation block and a symbol action cannot ask it.</b> Reaching for a semantic model
    ///     from a symbol action is <c>RS1030</c>; the supported spelling is to open the symbol, let
    ///     the block actions run over its members, and report at the end.
    /// </remarks>
    static void Inspect(
        SymbolStartAnalysisContext context,
        INamedTypeSymbol component,
        INamedTypeSymbol? readOnlySignal
    ) {
        if (context.Symbol is not INamedTypeSymbol type || type.TypeKind != TypeKind.Class || !Derives(type, component)) {
            return;
        }

        var plain = new List<IPropertySymbol>();

        foreach (var member in type.GetMembers()) {
            if (member is IPropertySymbol property
                && IsParameter(property)
                && !IsReactive(property.Type, readOnlySignal)
                && !IsCallback(property.Type)) {
                plain.Add(property);
            }
        }

        if (plain.Count == 0) {
            return;
        }

        var subscribable = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        context.RegisterOperationBlockAction(
            block => {
                if (block.OwningSymbol is not IMethodSymbol { MethodKind: MethodKind.PropertyGet } getter
                    || getter.AssociatedSymbol is not IPropertySymbol read) {
                    return;
                }

                foreach (var body in block.OperationBlocks) {
                    foreach (var operation in body.DescendantsAndSelf()) {
                        if (operation.Type is { } value && IsReactive(value, readOnlySignal)) {
                            subscribable[read.Name] = 0;

                            return;
                        }
                    }
                }
            }
        );

        context.RegisterSymbolEndAction(
            end => {
                foreach (var property in plain) {
                    if (subscribable.ContainsKey(property.Name)) {
                        continue;
                    }

                    foreach (var location in property.Locations) {
                        if (!location.IsInSource) {
                            continue;
                        }

                        end.ReportDiagnostic(
                            Diagnostic.Create(
                                PlainParameter,
                                Reported(location),
                                type.Name,
                                property.Name,
                                property.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                            )
                        );
                    }
                }
            }
        );
    }

    /// <summary>Where the author can act on the declaration.</summary>
    /// <remarks>
    ///     ⚠ <b>An external location is what carries a diagnostic out of generated code, and it is
    ///     also what stops one escaping that should not.</b> A <c>#line</c> in the emitted C# means
    ///     the characters belong to a <c>.vxml</c>, so the diagnostic is re-created against that
    ///     file and no longer sits in any syntax tree — which is why Roslyn's generated-code filter,
    ///     which asks a location for its <c>SourceTree</c>, lets it through. Generated code with no
    ///     mapping keeps its original location and is dropped by that same filter.
    /// </remarks>
    static Location Reported(Location location) {
        var mapped = location.GetMappedLineSpan();

        return mapped.HasMappedPath ? Location.Create(mapped.Path, location.SourceSpan, mapped.Span) : location;
    }

    /// <summary>Whether the type is a <c>Component</c>, however far down.</summary>
    static bool Derives(INamedTypeSymbol type, INamedTypeSymbol component) {
        for (var current = type.BaseType; current is not null; current = current.BaseType) {
            if (SymbolEqualityComparer.Default.Equals(current, component)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether markup could assign it.</summary>
    /// <remarks>
    ///     ⚠ <b>The setter's accessibility rather than the property's, and an instance rather than a
    ///     static.</b> <c>ComponentEmitter</c> writes <c>n1.Prop = …</c> against the instance it just
    ///     created, so a property with a private setter is not a parameter however public its getter
    ///     is — it is a component's own state, which is exactly what a computed <c>Accent =&gt;
    ///     Inject&lt;…&gt;()</c> is.
    /// </remarks>
    static bool IsParameter(IPropertySymbol property) =>
        !property.IsStatic
        && !property.IsIndexer
        && property.DeclaredAccessibility == Accessibility.Public
        && property.SetMethod is { DeclaredAccessibility: Accessibility.Public };

    /// <summary>Whether an effect can subscribe to reading it.</summary>
    /// <remarks>
    ///     Anything from <c>Vixen.Ui.Reactive</c> counts, which covers <c>Signal</c>,
    ///     <c>Computed</c>, <c>LinkedSignal</c>, <c>CollectionSignal</c> and <c>SignalDictionary</c>
    ///     without this file having to list them and go stale when a sixth is added. An
    ///     <c>IReadOnlySignal&lt;T&gt;</c> from anywhere counts too, because a parameter typed as the
    ///     interface is the shape a component that only reads a value should ask for.
    /// </remarks>
    static bool IsReactive(ITypeSymbol type, INamedTypeSymbol? readOnlySignal) {
        if (type.ContainingNamespace?.ToDisplayString() == SignalNamespace) {
            return true;
        }

        if (readOnlySignal is null) {
            return false;
        }

        if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, readOnlySignal)) {
            return true;
        }

        foreach (var contract in type.AllInterfaces) {
            if (SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, readOnlySignal)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether it is a callback, which is invoked rather than read.</summary>
    /// <remarks>
    ///     ⚠ <b>Not an exception carved out to quieten the rule.</b> A parent handing a child an
    ///     <c>Action</c> is handing it something to call; nothing subscribes to the delegate itself,
    ///     so "it cannot follow its source" is not a sentence about it. A signal-backed
    ///     <c>Action</c> parameter would be a component that can change which callback it holds,
    ///     which is a different and much rarer thing to want.
    /// </remarks>
    static bool IsCallback(ITypeSymbol type) => type.TypeKind == TypeKind.Delegate;
}
