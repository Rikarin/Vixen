// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Shaders;

namespace Vixen.Rendering;

/// <summary>How a stage orders the work it collected.</summary>
public enum RenderSortMode {
    /// <summary>
    ///     Group first, then near to far — for opaque geometry.
    /// </summary>
    /// <remarks>
    ///     Group above depth, not the other way round: early-Z makes front-to-back worth having, but
    ///     a pipeline switch costs far more than a few overdrawn pixels. Sorting purely by depth is
    ///     the classic mistake that makes a scene slower the better it is culled.
    /// </remarks>
    FrontToBack,

    /// <summary>
    ///     Far to near, ignoring grouping — for anything blended.
    /// </summary>
    /// <remarks>
    ///     Grouping is not merely less important here, it is <em>wrong</em>: blending is
    ///     order-dependent, so reordering two overlapping transparent objects to save a pipeline
    ///     change changes the image.
    /// </remarks>
    BackToFront,

    /// <summary>Group only, leaving depth out of it — for UI and anything else already ordered.</summary>
    ByGroup
}

/// <summary>
///     One list of work with one ordering: "Opaque", "Transparent", "ShadowCaster", "GBuffer".
/// </summary>
/// <remarks>
///     A stage is deliberately not a pass. A pass is where things are drawn — a render-graph node
///     with attachments and barriers; a stage is <em>which</em> things and in what order. One stage
///     feeds several passes (an opaque stage draws into every shadow cascade), and one pass may draw
///     several stages, so binding the two together would mean a shadow map needing its own copy of
///     "the opaque list".
/// </remarks>
public sealed class RenderStage(string name, RenderSortMode sortMode = RenderSortMode.FrontToBack) {
    /// <summary>The stage's name, for logging and for the compositor asset to refer to it by.</summary>
    public string Name { get; } = name;

    /// <summary>How this stage's work is ordered.</summary>
    public RenderSortMode SortMode { get; } = sortMode;

    /// <summary>How this stage's fragments combine with what is already in the target.</summary>
    /// <remarks>
    ///     <para>
    ///         State belongs to the stage and formats belong to the pass, and the division is not
    ///         arbitrary: "Opaque" means depth-written and unblended <em>wherever</em> it is drawn,
    ///         while what it is drawn into changes with every pass that draws it. A stage that
    ///         carried formats could not feed four shadow cascades and a G-buffer; a pass that
    ///         carried blend state could not draw an opaque stage and a transparent one.
    ///     </para>
    ///     <para>
    ///         One blend state for every colour target, which is what a G-buffer wants and what a
    ///         forward pass wants. A stage needing per-target blending is rare enough to deserve its
    ///         own <see cref="IPipelineDescriber" /> rather than a field every stage carries.
    ///     </para>
    /// </remarks>
    public BlendState Blend { get; set; } = BlendState.Opaque;

    /// <summary>The depth and stencil tests this stage's draws use.</summary>
    /// <remarks>
    ///     Defaults to testing and writing with the engine's reversed comparison. A transparent stage
    ///     wants <see cref="DepthStencilState.TestOnly" />, which is the other half of why its sort
    ///     mode ignores grouping: it is ordered by depth because nothing else orders it.
    /// </remarks>
    public DepthStencilState DepthStencil { get; set; } = DepthStencilState.Default;

    /// <summary>How this stage's triangles become fragments.</summary>
    /// <remarks>
    ///     A shadow-caster stage is the reason this is here rather than on the material: it wants
    ///     depth clamping and a depth bias that have nothing to do with the surface being drawn and
    ///     everything to do with what the pass is for.
    /// </remarks>
    public RasterizerState Rasterizer { get; set; } = RasterizerState.Default;

    /// <summary>
    ///     The shader every object in this stage is drawn with, overriding its material's.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         What makes a depth prepass a prepass and not a second full shading pass. A prepass
    ///         exists to fill depth as fast as possible, so drawing it with each object's own material
    ///         would run every fragment shader twice and cost more than the overdraw it was meant to
    ///         remove. <c>Library/Pipeline/DepthOnly.rvn</c> is a vertex stage and, unless it is alpha
    ///         tested, no fragment work at all.
    ///     </para>
    ///     <para>
    ///         A shadow-caster stage is the same argument, and the same fix: a shadow map records
    ///         depth, so a caster has no reason to evaluate a BRDF.
    ///     </para>
    ///     <para>
    ///         Null leaves the material's own shader alone, which is what a colour stage wants. The
    ///         override is the stage's rather than the material's because it is a property of what the
    ///         pass is <em>for</em> — the same mesh is drawn with its material in one stage and with
    ///         depth-only in another, in the same frame.
    ///     </para>
    /// </remarks>
    public string? ShaderName { get; set; }

    /// <summary>Whether the overriding shader fills its <c>compose</c> slots from the material.</summary>
    /// <remarks>
    ///     <para>
    ///         Only meaningful beside <see cref="ShaderName" />, and false by default because the
    ///         passes that override one mostly do not compose: <c>DepthOnly</c> and
    ///         <c>ShadowCaster</c> write depth and declare no slots, so handing them a material's
    ///         features would split their cache once per distinct material for variants that compile
    ///         to the same bytes.
    ///     </para>
    ///     <para>
    ///         A G-buffer stage is the exception and the reason this exists: <c>GBufferPass</c> does
    ///         declare <c>surface</c>, so its variant depends on the material's features exactly as
    ///         the forward pass's does.
    ///     </para>
    /// </remarks>
    public bool ShaderComposes { get; set; }

    /// <summary>What this stage supplies to the shader it imposes, where a material has nothing.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>A stage that overrides the shader owes it its values.</b> A material knows what
    ///         <c>albedo</c> is; it has no opinion about <c>ShadowCaster.opacityMap</c>, and it never
    ///         will, because that binding belongs to a pass the material has never heard of. Without
    ///         somewhere for those to come from, the caster's per-material set has bindings nothing
    ///         fills — and <see cref="EffectSetWriter" /> writes a set wholly or not at all, so the
    ///         set is never bound and every draw in the stage is refused.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A fallback, not an override.</b> The material is asked first, so an alpha-tested
    ///         caster still cuts out against the material's own opacity map; this is what fills the
    ///         gap for the far more common material that has none. Getting that order the other way
    ///         round would make every cut-out in a level solid, which reads as a shadow bug and is a
    ///         binding one.
    ///     </para>
    ///     <para>
    ///         Empty for a stage that draws materials with their own shader, where there is no second
    ///         shader to owe anything to.
    ///     </para>
    /// </remarks>
    public ParameterCollection Parameters { get; } = new();

    /// <summary>The stage's index, assigned when it is added to a <see cref="RenderSystem" />.</summary>
    public int Index { get; internal set; } = -1;

    /// <summary>This stage alone, as a mask.</summary>
    public RenderStageMask Mask => RenderStageMask.Of(Index);

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>A set of stages, as a bit per stage index.</summary>
/// <remarks>
///     <para>
///         A mask rather than a list, because the culling loop asks "does this object appear in any
///         stage this view wants" once per object per view, and that has to be one <c>and</c>.
///     </para>
///     <para>
///         64 stages is the ceiling. Stride's shipped compositors use fewer than ten; a project
///         needing more has a compositor problem rather than a bit-width problem, and finding out at
///         <see cref="RenderSystem.AddStage" /> is better than a silent wrap.
///     </para>
/// </remarks>
public readonly record struct RenderStageMask(ulong Bits) {
    /// <summary>How many distinct stages a mask can hold.</summary>
    public const int Capacity = 64;

    /// <summary>The empty set.</summary>
    public static RenderStageMask None => default;

    /// <summary>Every stage there could be.</summary>
    /// <remarks>
    ///     Every representable stage rather than every <em>declared</em> one, which costs nothing: a
    ///     bit with no stage behind it intersects with no object. What it is for is a view that draws
    ///     regardless of staging — see <see cref="Compositor.VisibilityBufferRenderer.Stages" />, whose
    ///     default this is.
    /// </remarks>
    public static RenderStageMask All => new(ulong.MaxValue);

    /// <summary>The set holding just one stage.</summary>
    public static RenderStageMask Of(int stageIndex) {
        ArgumentOutOfRangeException.ThrowIfNegative(stageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(stageIndex, Capacity);
        return new(1UL << stageIndex);
    }

    /// <summary>Whether the set is empty.</summary>
    public bool IsEmpty => Bits == 0;

    /// <summary>Whether a stage is in the set.</summary>
    public bool Contains(int stageIndex) => (Bits & (1UL << stageIndex)) != 0;

    /// <summary>Whether the two sets have any stage in common.</summary>
    public bool Intersects(RenderStageMask other) => (Bits & other.Bits) != 0;

    /// <summary>The union of two sets.</summary>
    public static RenderStageMask operator |(RenderStageMask left, RenderStageMask right) =>
        new(left.Bits | right.Bits);

    /// <summary>The stages both sets hold.</summary>
    public static RenderStageMask operator &(RenderStageMask left, RenderStageMask right) =>
        new(left.Bits & right.Bits);

    /// <summary>The union of two sets, for callers that cannot use the operator.</summary>
    public RenderStageMask Union(RenderStageMask other) => this | other;

    /// <summary>The stages both sets hold, for callers that cannot use the operator.</summary>
    public RenderStageMask Intersect(RenderStageMask other) => this & other;

    /// <summary>This set without the stages the other holds.</summary>
    /// <param name="other">The stages to take out.</param>
    /// <returns>The remainder.</returns>
    /// <remarks>
    ///     ⚠ <b>A subtraction rather than a complement, because a complement of a mask is every bit
    ///     with no stage behind it as well.</b> <see cref="All" /> is deliberately every
    ///     <em>representable</em> stage and not every declared one, so <c>All.Except(x)</c> is a set
    ///     holding 63 stages that do not exist — harmless where it is intersected against an object's
    ///     mask, and a surprise anywhere it is read as a list. Taking one set out of another never
    ///     invents a bit.
    /// </remarks>
    public RenderStageMask Except(RenderStageMask other) => new(Bits & ~other.Bits);

    /// <summary>The stages in the set, ascending.</summary>
    public IEnumerable<int> Indices() {
        for (var i = 0; i < Capacity; i++) {
            if (Contains(i)) {
                yield return i;
            }
        }
    }

    /// <inheritdoc />
    public override string ToString() => IsEmpty ? "{}" : "{" + string.Join(", ", Indices()) + "}";
}
