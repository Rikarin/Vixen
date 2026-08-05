// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Rendering.Compositor;

namespace Vixen.Rendering.Water;

/// <summary>The water pass, as a document names it.</summary>
/// <remarks>
///     <para>
///         <b>A node in a <c>.vxcompositor</c>, which means a project that has no water does not pay
///         for the copy</b> —
///         [35 § D8](../../docs/plan/35-water.md#d8-the-surface-is-a-pass-between-lighting-and-translucency-and-its-reflections-are-l5s).
///         The <c>!Copy</c> ahead of it is culled with its target when nothing reads the snapshot, so
///         a document carrying both costs nothing in a scene where no surface pass ran.
///     </para>
///     <para>
///         ⚠ <b><see cref="Behind" /> must name a copy and not the scene colour itself.</b> Reading
///         the target a pass is writing is undefined behaviour, and the whole reason § B1 called the
///         copy the blocker rather than the pass. The graph refuses the case where the two names are
///         equal — see <see cref="WaterRenderer" /> — because a document that made that mistake would
///         otherwise render on one driver and not another.
///     </para>
/// </remarks>
[DataContract("Water")]
public sealed record WaterAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The target it writes — the frame's scene colour.</summary>
    public string Output { get; init; } = "SceneColour";

    /// <summary>The copy of the frame so far, which is what is behind the water.</summary>
    public string Behind { get; init; } = "SceneColourCopy";

    /// <summary>The opaque scene's depth.</summary>
    public string SceneDepth { get; init; } = "SceneDepth";

    /// <summary>The surface plane: device depth in <c>r</c>, coverage in <c>g</c>.</summary>
    public string Surface { get; init; } = "WaterSurface";

    /// <summary>The surface's world normal in <c>xyz</c> and its foam in <c>a</c>.</summary>
    public string Normal { get; init; } = "WaterNormal";

    /// <summary>Doc 19 § L5's reflection plane, or empty to compile the variant without it.</summary>
    public string Reflections { get; init; } = string.Empty;

    /// <summary>Which view's camera the two distances are reconstructed against.</summary>
    public string View { get; init; } = string.Empty;

    /// <summary>How much light the medium scatters out of a ray per metre, per channel.</summary>
    public Vector3 Scattering { get; init; } = new(0.03f, 0.06f, 0.09f);

    /// <summary>How much it absorbs per metre, per channel.</summary>
    public Vector3 Absorption { get; init; } = new(0.35f, 0.06f, 0.02f);

    /// <summary>Henyey–Greenstein anisotropy. Water is forward-scattering, around 0.7.</summary>
    public float PhaseG { get; init; } = 0.7f;

    /// <summary>§ D8's scale on what is behind the water. One is the physical answer.</summary>
    public Vector3 BehindScale { get; init; } = Vector3.One;

    /// <summary>The radiance the volume scatters.</summary>
    public Vector3 SunColour { get; init; } = new(1f, 0.96f, 0.9f);

    /// <summary>Which way the light travels.</summary>
    /// <remarks>
    ///     ⚠ Left unstated, the water lights with a noon sun whatever the sky in the same document
    ///     says — the forward-scattering peak lands in the wrong place, which reads as a lake lit
    ///     from a different day than its sky.
    /// </remarks>
    public Vector3 SunDirection { get; init; } = new(0f, -1f, 0f);

    /// <summary>What arrives from the whole sky. ⚠ Without it, deep water is black.</summary>
    public Vector3 SkyColour { get; init; } = new(0.35f, 0.45f, 0.6f);

    /// <summary>Water against air. ⚠ Not a base colour.</summary>
    public float SurfaceF0 { get; init; } = 0.02f;

    /// <summary>What foam is where the surface plane says there is some.</summary>
    public Vector3 FoamColour { get; init; } = new(0.9f, 0.93f, 0.95f);

    /// <summary>Whether foam is blended over the result at all.</summary>
    public bool Foam { get; init; } = true;
}

/// <summary>Builds this assembly's node kinds for a document that names them.</summary>
/// <remarks>
///     Registered on <c>CompositorBuilder.Factories</c>, which is asked <em>after</em> the built-ins —
///     so a factory cannot quietly replace a node kind the document's schema already defines.
/// </remarks>
public sealed class WaterRendererFactory : ISceneRendererFactory {
    /// <inheritdoc />
    public SceneRenderer? Create(ISceneRendererAsset declared, CompositorBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        return declared is WaterAsset water ? Water(water, builder) : null;
    }

    static WaterRenderer Water(WaterAsset declared, CompositorBuilder builder) =>
        new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Output = declared.Output,
            Behind = declared.Behind,
            SceneDepth = declared.SceneDepth,
            Surface = declared.Surface,
            Normal = declared.Normal,
            Reflections = declared.Reflections,
            View = declared.View is { Length: > 0 } name ? builder.Views.GetValueOrDefault(name) : null,
            Scattering = declared.Scattering,
            Absorption = declared.Absorption,
            PhaseG = declared.PhaseG,
            BehindScale = declared.BehindScale,
            SunColour = declared.SunColour,
            SunDirection = declared.SunDirection,
            SkyColour = declared.SkyColour,
            SurfaceF0 = declared.SurfaceF0,
            FoamColour = declared.FoamColour,
            Foam = declared.Foam,
            Modules = builder.Modules,
            Samplers = builder.Samplers,
            Allocator = builder.Descriptors,
            Device = builder.Device
        };
}
