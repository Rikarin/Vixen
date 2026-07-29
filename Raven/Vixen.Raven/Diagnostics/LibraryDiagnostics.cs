// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;

namespace Vixen.Raven.Diagnostics;

/// <summary>
///     Stable descriptors for the library phase — writing a <c>.rvnlib</c> and linking one
///     into a compilation. The <c>RVN5xxx</c> range is reserved for it; <c>RVN1xxx</c> is
///     syntax, <c>RVN2xxx</c> semantics, <c>RVN3xxx</c> lowering and <c>RVN4xxx</c> the
///     backends.
/// </summary>
/// <remarks>
///     A range of its own rather than an extension of lowering's, because these are the only
///     diagnostics that can be produced with no source in the compilation at all: a reader
///     rejecting an artefact has a file and a symbol name to name, and nothing else.
/// </remarks>
public static class LibraryDiagnostics {
    const string Export = "Library";
    const string Link = "Link";

    // --- Writing ----------------------------------------------------------

    /// <summary>
    ///     A body being exported reads a shader-level binding, which a consumer cannot
    ///     supply.
    /// </summary>
    /// <remarks>
    ///     Reported where it can be fixed — in the library — rather than in every consumer.
    ///     A binding belongs to the shader that declares it: its <c>(set, binding)</c> pair is
    ///     assigned per effect, so linking the function that reads it into another shader
    ///     would name storage that shader never declared. GLSL would emit an undeclared
    ///     identifier and SPIR-V would fail on a missing key, which is the same class of
    ///     silent failure unflattened inheritance produced.
    /// </remarks>
    public static readonly DiagnosticDescriptor BindingNotExportable = new(
        "RVN5001",
        "Exported function reads a shader binding",
        "'{0}' reads the shader binding '{1}', so it cannot be exported to a library: a binding "
        + "belongs to the shader that declares it and is not linked into a consumer",
        Export,
        DiagnosticSeverity.Error
    );

    /// <summary>
    ///     A shader's entry point is not part of what a library exports.
    /// </summary>
    /// <remarks>
    ///     Informational rather than silent: a library supplies types and functions, and a
    ///     stage is generated per effect from the shader that declares it. An author who wrote
    ///     <c>[FragmentShader]</c> in a library file believes something about what shipping it
    ///     does.
    /// </remarks>
    public static readonly DiagnosticDescriptor EntryPointNotExported = new(
        "RVN5002",
        "Entry point is not exported",
        "The {1} entry point '{0}' is not exported: a library supplies types and functions, and a "
        + "pipeline stage is generated per effect",
        Export,
        DiagnosticSeverity.Info
    );

    /// <summary>
    ///     A body being exported touches a <c>stream</c>, whose location belongs to the consuming
    ///     shader.
    /// </summary>
    /// <remarks>
    ///     A separate refusal from <see cref="BindingNotExportable" /> because the reason is
    ///     different, and the difference is what tells the author what to do. A binding cannot
    ///     travel because its descriptor belongs to one shader; a stream cannot travel because its
    ///     <em>location</em> is the consuming shader's stream list, so linking the function would
    ///     mean matching the two shaders' streams by name — the flattening half of the mixin
    ///     problem (docs/plan/07 § J), not a serialization gap. Inside one compilation a stream
    ///     crosses any number of functions freely; it is only the artefact boundary it does not
    ///     cross.
    /// </remarks>
    public static readonly DiagnosticDescriptor StreamNotExportable = new(
        "RVN5007",
        "Exported function uses a stream",
        "'{0}' uses the stream '{1}', so it cannot be exported to a library: a stream's location "
        + "belongs to the shader that declares it, and matching streams across libraries by name is "
        + "not implemented",
        Export,
        DiagnosticSeverity.Error
    );

    /// <summary>
    ///     A body being exported touches <c>groupshared</c> storage, which belongs to the consuming
    ///     dispatch's workgroups.
    /// </summary>
    /// <remarks>
    ///     A third refusal for a third reason, on the argument <see cref="StreamNotExportable" />
    ///     makes: what a library cannot carry is anything whose identity is decided by the shader
    ///     that ends up holding it. A binding's is its descriptor, a stream's is its location, and a
    ///     group-shared variable's is the workgroup — which belongs to the dispatch the consumer
    ///     declares, not to the library. The fix is the same shape in every case: take the value as
    ///     a parameter and let the consuming shader own the storage.
    /// </remarks>
    public static readonly DiagnosticDescriptor GroupSharedNotExportable = new(
        "RVN5008",
        "Exported function uses group-shared storage",
        "'{0}' uses the group-shared variable '{1}', so it cannot be exported to a library: "
        + "workgroup storage belongs to the dispatch that declares it",
        Export,
        DiagnosticSeverity.Error
    );

    /// <summary>
    ///     A library was compiled with a permutation key read, so its value is baked into the
    ///     exported bodies.
    /// </summary>
    /// <remarks>
    ///     The one thing a library cannot carry. A <c>[Permutation]</c> key is resolved at compile
    ///     time — that is what makes the dead branch disappear — so a body lowered when the library
    ///     was built has one variant of it, and a consumer's own <c>--define</c> cannot reach back
    ///     in. A library should take the value as a parameter instead. Said rather than left to be
    ///     discovered, because the symptom is a define that appears to be ignored.
    /// </remarks>
    public static readonly DiagnosticDescriptor PermutationBakedIn = new(
        "RVN5006",
        "Permutation key is baked into the library",
        "The permutation key '{0}' was read while building this library, so the value it had is baked "
        + "into the exported bodies and a consumer cannot vary it",
        Export,
        DiagnosticSeverity.Warning
    );

    // --- Linking ----------------------------------------------------------

    /// <summary>A referenced type has the same qualified name as one declared in source.</summary>
    /// <remarks>
    ///     Source wins, matching every other compiler with a reference model, but silently
    ///     preferring one of two same-named types is how a shader ends up bound against the
    ///     definition its author was not reading.
    /// </remarks>
    public static readonly DiagnosticDescriptor ReferenceHiddenBySource = new(
        "RVN5003",
        "Referenced type is hidden by a source declaration",
        "'{0}' from library '{1}' is hidden by the declaration of the same name in this compilation",
        Link,
        DiagnosticSeverity.Warning
    );

    /// <summary>A library names a type that none of the supplied references declares.</summary>
    /// <remarks>
    ///     Reported rather than absorbed into an error type: a missing reference is a
    ///     command-line mistake, and its symptom without this is a member that mysteriously
    ///     cannot be found on a type whose source nobody has.
    /// </remarks>
    public static readonly DiagnosticDescriptor ReferenceTypeUnresolved = new(
        "RVN5004",
        "Referenced type could not be resolved",
        "Library '{0}' refers to the type '{1}', which none of the supplied references declares",
        Link,
        DiagnosticSeverity.Error
    );

    /// <summary>Two references declare the same library name.</summary>
    public static readonly DiagnosticDescriptor DuplicateReference = new(
        "RVN5005",
        "Duplicate library reference",
        "The library '{0}' is referenced more than once; the first reference is used",
        Link,
        DiagnosticSeverity.Warning
    );
}
