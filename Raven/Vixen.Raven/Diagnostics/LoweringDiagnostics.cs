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

    /// <summary>A push-constant block larger than every Vulkan implementation is required to offer.</summary>
    /// <remarks>
    ///     <para>
    ///         128 bytes is <c>maxPushConstantsSize</c>'s guaranteed minimum — small on purpose,
    ///         because the whole point of a push constant is that it travels in the command buffer
    ///         rather than through a descriptor. A shader that goes over it still compiles and will
    ///         run on the many devices that offer more, so this is a warning: the alternative would
    ///         refuse a shader that is correct for its target.
    ///     </para>
    ///     <para>
    ///         Measured std430, which is what both targets lay a push-constant block out with — so
    ///         the number here is the number the host will have to fit.
    ///     </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor PushConstantsOverGuaranteedSize = new(
        "RVN3007",
        "Push constants exceed the guaranteed size",
        "Shader '{0}' pushes {1} bytes, and a Vulkan implementation is only required to offer 128; "
        + "move the values that change least often into a uniform binding",
        Lowering,
        DiagnosticSeverity.Warning
    );

    /// <summary>Two <c>[Shared]</c> declarations of one name that are not the same resource.</summary>
    /// <remarks>
    ///     <para>
    ///         A shared binding is recognised by the name several features wrote, so two features
    ///         that write the same name mean the same resource — and if they disagree about what it
    ///         is, one of them is wrong and no compiler can say which. Refused here rather than
    ///         collapsed to whichever came first, which would compile a feature against a texture it
    ///         did not declare.
    ///     </para>
    ///     <para>
    ///         The set is part of it as well as the kind. Two declarations of <c>textures</c>, one
    ///         <c>[PerFrame]</c> and one <c>[PerMaterial]</c>, are two descriptor sets and cannot be
    ///         one binding however identical the type is.
    ///     </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor SharedBindingsDisagree = new(
        "RVN3011",
        "Shared bindings disagree",
        "'{0}' is declared [Shared] more than once in '{1}' and the declarations do not match: {2}. "
        + "A shared binding is one resource recognised by its name, so every declaration of it has to "
        + "be the same kind in the same set",
        Lowering,
        DiagnosticSeverity.Error
    );

    /// <summary>A <c>discard</c> reachable from a stage that has no fragment to throw away.</summary>
    /// <remarks>
    ///     <para>
    ///         Reachability rather than where the keyword is written, because a helper is shared: a
    ///         cutout test called from both the depth prepass and a compute pass is only wrong in the
    ///         second, and the file it is written in cannot tell. This is the same reasoning that
    ///         decides which functions belong to a stage in the first place.
    ///     </para>
    ///     <para>
    ///         An error rather than a warning, and the strongest reason is that the alternative is
    ///         not silence: SPIR-V's <c>OpKill</c> is valid only under the Fragment execution model,
    ///         so this would leave <c>spirv-val</c> to say it — about a module, with no span.
    ///     </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor DiscardOutsideFragmentStage = new(
        "RVN3008",
        "Discard outside a fragment stage",
        "'discard' is reachable from {0} entry point '{1}', and only a fragment stage has a fragment "
        + "to throw away",
        Lowering,
        DiagnosticSeverity.Error
    );

    /// <summary>
    ///     Workgroup storage — a <c>groupshared</c> variable or a barrier — reached from a stage
    ///     that has no workgroups.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Only a compute dispatch has groups. A vertex or fragment invocation belongs to
    ///         nothing an author can name, so there is no "shared" for the storage to be shared
    ///         between and nobody for a barrier to wait for.
    ///     </para>
    ///     <para>
    ///         Reachability rather than where the declaration sits, for the reason
    ///         <c>RVN3008</c> gives about <c>discard</c>: a helper belongs to whichever stages call
    ///         it, and the file it is written in cannot say which those are. A tile reduction shared
    ///         by a compute pass and a fullscreen fragment pass is wrong only in the second.
    ///     </para>
    ///     <para>
    ///         An error rather than a warning, and again because the alternative is not silence:
    ///         SPIR-V's <c>Workgroup</c> storage class and an <c>OpControlBarrier</c> at workgroup
    ///         scope are valid only under the GLCompute execution model, so this would otherwise
    ///         reach <c>spirv-val</c> — about a module, with no span.
    ///     </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor WorkgroupStorageOutsideCompute = new(
        "RVN3012",
        "Workgroup storage outside a compute stage",
        "{0} is reachable from {1} entry point '{2}', and only a compute dispatch has workgroups",
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
