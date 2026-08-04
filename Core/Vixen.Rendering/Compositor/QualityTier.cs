// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Rendering.Compositor;

/// <summary>The scalability tier — how much the frame spends, never what it does.</summary>
/// <remarks>
///     <para>
///         Unreal's ladder, deliberately: four names everybody already knows, each folding the
///         numeric sub-knobs (cascade resolutions, probe budgets, march steps, tap counts) that
///         docs/plan/39's table assigns per tier. A feature is turned on and off by its own knob on
///         the preset node; the tier only decides how well the enabled ones run — with one stated
///         exception: the cheapest tiers drop whole post passes, because a pass that runs at any
///         cost is not a Low tier.
///     </para>
///     <para>
///         The enum lives beside the compositor schema rather than in the effect set because the
///         tier is the one preset fact a <em>host</em> states — <c>GraphicsOptions</c> carries the
///         platform's pick and <see cref="CompositorBuilder.Quality" /> hands it to the document
///         transform — and the hosting assembly does not reference the effect set. What each name
///         folds into numbers is the effect set's business, in its <c>RenderQuality</c> waterfall.
///     </para>
/// </remarks>
public enum QualityTier {
    /// <summary>The integrated-GPU floor: no GPU culling, tonemap-only post.</summary>
    Low,

    /// <summary>Adds GPU culling, bloom and fog over <see cref="Low" />.</summary>
    Medium,

    /// <summary>The full post chain at moderate numbers — the default.</summary>
    High,

    /// <summary>The full chain at the numbers a capture is taken at.</summary>
    Epic
}
