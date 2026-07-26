// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;

namespace Vixen.Raven.Diagnostics;

/// <summary>
///     Stable descriptors for lowering and IR verification. The <c>RVN3xxx</c> range
///     is reserved for this phase; <c>RVN1xxx</c> is syntax and <c>RVN2xxx</c>
///     semantics.
/// </summary>
public static class LoweringDiagnostics {
    const string Lowering = "Lowering";
    const string Verification = "IR";

    /// <summary>
    ///     The semantic model accepted the type, but it has no GPU representation —
    ///     <c>string</c>, a tuple, a nullable, a lambda.
    /// </summary>
    public static readonly DiagnosticDescriptor TypeNotRepresentable = new(
        "RVN3001",
        "Type is not representable on the target",
        "Type '{0}' cannot be lowered: it has no representation on the target",
        Lowering,
        DiagnosticSeverity.Error
    );

    /// <summary>A construct the binder understands but lowering does not implement yet.</summary>
    public static readonly DiagnosticDescriptor ConstructNotSupported = new(
        "RVN3002",
        "Construct is not supported in lowering",
        "{0} cannot be lowered yet",
        Lowering,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor NotAddressable = new(
        "RVN3003",
        "Target is not addressable",
        "The target of this assignment has no storage the target can address",
        Lowering,
        DiagnosticSeverity.Error
    );

    public static readonly DiagnosticDescriptor MissingBody = new(
        "RVN3004",
        "Member has no body",
        "'{0}' has no body, so there is nothing to lower",
        Lowering,
        DiagnosticSeverity.Error
    );

    /// <summary>A stream written by a stage nothing downstream reads from.</summary>
    /// <remarks>
    ///     A fragment stage's outputs are render targets — location 0 is target 0 — so a stream
    ///     written there goes nowhere. A warning rather than an error, on the <c>RVN2091</c>
    ///     pattern: the shader still compiles and still does what it says, but the author believes
    ///     something untrue about where the value ends up.
    /// </remarks>
    public static readonly DiagnosticDescriptor StreamNotConsumed = new(
        "RVN3005",
        "Stream is written by a stage nothing reads it from",
        "Stream '{0}' is written by '{1}', but a fragment stage's outputs are render targets rather "
        + "than interstage values, so nothing downstream reads it",
        Lowering,
        DiagnosticSeverity.Warning
    );

    /// <summary>A stream used by a compute stage, which has no interstage interface at all.</summary>
    /// <remarks>
    ///     An error rather than <c>RVN3005</c>'s warning, because there is no honest thing to emit.
    ///     A stream is a location in the pipeline's interface and a compute dispatch has no
    ///     pipeline: GLSL took the store and wrote to an identifier it never declared, which
    ///     <c>glslc</c> rejects but nothing in Raven caught.
    /// </remarks>
    public static readonly DiagnosticDescriptor StreamInComputeStage = new(
        "RVN3006",
        "Stream used by a compute stage",
        "Stream '{0}' is used by compute entry point '{1}', but a compute stage has no interstage "
        + "interface for it to live in",
        Lowering,
        DiagnosticSeverity.Error
    );

    // --- Verification -----------------------------------------------------

    public static readonly DiagnosticDescriptor MalformedIr = new(
        "RVN3010",
        "Malformed IR",
        "{0}",
        Verification,
        DiagnosticSeverity.Error
    );
}
