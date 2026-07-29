// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Editor.Inspector;

namespace Vixen.Editor.App;

/// <summary>What the world looks like when nothing is drawn in front of it.</summary>
[DataContract("WorldEnvironment")]
public sealed class EnvironmentSettings {
    /// <summary>What the sky is, where nothing else is drawn.</summary>
    [Inspector]
    [Tooltip("The colour the frame is cleared to where no geometry covers it.")]
    public Color4 Background { get; set; } = new(0.09f, 0.10f, 0.12f, 1f);

    /// <summary>The light that arrives from everywhere.</summary>
    [Inspector]
    [Tooltip("Light with no direction, applied to every surface. Doc 19's fallback where GI has no answer.")]
    public Color4 Ambient { get; set; } = new(0.05f, 0.06f, 0.08f, 1f);

    /// <summary>How much of it.</summary>
    [Inspector]
    [Range(0f, 8f)]
    public float AmbientIntensity { get; set; } = 1f;

    /// <summary>Whether distance fades towards the background colour.</summary>
    [Inspector]
    public bool Fog { get; set; }

    /// <summary>Where it starts, in metres.</summary>
    [Inspector]
    [Range(0f, 10_000f)]
    public float FogStart { get; set; } = 20f;

    /// <summary>And where it is total.</summary>
    [Inspector]
    [Range(0f, 10_000f)]
    public float FogEnd { get; set; } = 400f;
}

/// <summary>The dynamic global illumination doc 19 settled on, per scene.</summary>
/// <remarks>
///     ⚠ <b>There is nothing here about lightmaps, and that is doc 19's decision rather than an
///     omission.</b> Baked lightmaps are retired, so a lighting panel is about the three things a
///     dynamic solution has budgets for: how far the distance fields reach, how densely the
///     irradiance probes are placed, and how much of the surface cache a frame may spend.
/// </remarks>
[DataContract("WorldLighting")]
public sealed class LightingSettings {
    /// <summary>Whether the scene's global illumination is computed at all.</summary>
    [Inspector]
    [Tooltip("Off is a scene lit only by its own lights and the ambient term above.")]
    public bool GlobalIllumination { get; set; } = true;

    /// <summary>How far from the camera the signed distance fields are kept, in metres.</summary>
    [Inspector]
    [Range(1f, 2_000f)]
    [Tooltip("The radius the global distance field covers. Beyond it, tracing falls back to the sky.")]
    public float DistanceFieldRange { get; set; } = 200f;

    /// <summary>How coarse the coarsest clipmap level is, in metres per voxel.</summary>
    [Inspector]
    [Range(0.05f, 8f)]
    public float DistanceFieldVoxel { get; set; } = 0.4f;

    /// <summary>How far apart irradiance probes are placed, in metres.</summary>
    [Inspector]
    [Range(0.25f, 32f)]
    [Tooltip("Closer is more accurate and costs a probe update each. Doc 19's irradiance field spacing.")]
    public float ProbeSpacing { get; set; } = 2f;

    /// <summary>How many probes may be updated per frame.</summary>
    [Inspector]
    [Range(1, 4_096)]
    public int ProbeBudget { get; set; } = 256;

    /// <summary>How many surface-cache cards a frame may re-shade.</summary>
    [Inspector]
    [Range(1, 16_384)]
    [Tooltip("The surface cache's per-frame budget. Lower is cheaper and slower to respond to a light moving.")]
    public int SurfaceCacheBudget { get; set; } = 1_024;

    /// <summary>How many bounces the solution carries.</summary>
    [Inspector]
    [Range(0, 4)]
    public int Bounces { get; set; } = 1;
}

/// <summary>What the scene's physics does when nobody says otherwise.</summary>
[DataContract("WorldPhysics")]
public sealed class PhysicsSettings {
    /// <summary>The acceleration everything falls under, in metres per second squared.</summary>
    [Inspector]
    public Vector3 Gravity { get; set; } = new(0f, -9.81f, 0f);

    /// <summary>How many times a second the simulation steps.</summary>
    [Inspector]
    [Range(10f, 240f)]
    public float StepRate { get; set; } = 60f;

    /// <summary>How many solver iterations each step takes.</summary>
    [Inspector]
    [Range(1, 32)]
    public int SolverIterations { get; set; } = 8;
}

/// <summary>What a navigation mesh is baked with, per scene.</summary>
/// <remarks>
///     A mirror of <c>NavMeshBuildSettings</c>'s numbers rather than the struct itself, for the
///     reason every settings type in this application gives: the runtime's type is what a baker
///     takes, and an <c>[Inspector]</c> attribute on it would be a runtime assembly referencing an
///     editor one. <see cref="EditorApplication" />'s navigation panel is what translates.
/// </remarks>
[DataContract("WorldNavigation")]
public sealed class NavigationSettings {
    /// <summary>How wide the agent is, in metres.</summary>
    [Inspector]
    [Range(0.05f, 10f)]
    public float AgentRadius { get; set; } = 0.4f;

    /// <summary>How tall.</summary>
    [Inspector]
    [Range(0.1f, 10f)]
    public float AgentHeight { get; set; } = 1.8f;

    /// <summary>The tallest step it can walk up, in metres.</summary>
    [Inspector]
    [Range(0f, 4f)]
    public float AgentClimb { get; set; } = 0.4f;

    /// <summary>The steepest slope it can stand on, in degrees.</summary>
    [Inspector]
    [Range(0f, 89f)]
    public float AgentSlope { get; set; } = 45f;

    /// <summary>How large one voxel of the bake is, horizontally, in metres.</summary>
    [Inspector]
    [Range(0.02f, 2f)]
    public float CellSize { get; set; } = 0.2f;

    /// <summary>And vertically.</summary>
    [Inspector]
    [Range(0.02f, 2f)]
    public float CellHeight { get; set; } = 0.1f;

    /// <summary>How many cells across one bake tile is.</summary>
    [Inspector]
    [Range(16, 512)]
    public int TileSize { get; set; } = 64;
}

/// <summary>Everything that belongs to a scene rather than to the project or to one entity.</summary>
/// <remarks>
///     <para>
///         <b>Doc 20's B6: "the per-scene half of Project Settings".</b> A project's content target
///         is the same for every level; a level's gravity, sky, GI budget and agent size are not —
///         and putting them in <c>ProjectSettings/</c> would make the second level in a game the
///         moment somebody notices.
///     </para>
///     <para>
///         ⚠ <b>A sidecar beside the <c>.vxscene</c> rather than a block inside it, and the reason is
///         merge conflicts.</b> A scene file is where every entity anybody adds lands, so it is the
///         file two people on a team touch every day; the world settings change once a month and are
///         edited by one person. Keeping them apart means changing the fog does not conflict with
///         somebody else having moved a crate. It is the same argument doc 08 makes for <c>.meta</c>
///         files being beside their assets rather than inside a database.
///     </para>
///     <para>
///         ⚠ <b>Every group here is a <c>[DataContract]</c> with <c>[Inspector]</c> members and no
///         dialog code at all</b> — doc 11's "adding a setting is declaring a type", applied to the
///         scene. The panels below are three <c>InspectorView</c>s over three of these properties.
///     </para>
/// </remarks>
[DataContract("WorldSettings")]
public sealed class WorldSettings {
    /// <summary>The version this reader and writer speak.</summary>
    public const int Current = 1;

    /// <summary>What the sidecar beside a scene is called.</summary>
    public const string Extension = ".vxworld";

    /// <summary>Which version of the format this file is.</summary>
    public int Version { get; set; } = Current;

    /// <summary>The sky, the ambient term and the fog.</summary>
    public EnvironmentSettings Environment { get; set; } = new();

    /// <summary>The dynamic global illumination.</summary>
    public LightingSettings Lighting { get; set; } = new();

    /// <summary>Gravity and the solver.</summary>
    public PhysicsSettings Physics { get; set; } = new();

    /// <summary>What a navigation bake uses.</summary>
    public NavigationSettings Navigation { get; set; } = new();

    /// <summary>Where a scene's settings live.</summary>
    /// <param name="scenePath">The scene file's path.</param>
    /// <returns>The sidecar's path.</returns>
    public static string PathFor(string scenePath) {
        ArgumentException.ThrowIfNullOrEmpty(scenePath);

        return Path.ChangeExtension(scenePath, Extension);
    }

    /// <summary>Reads a scene's settings, or the defaults when it has none.</summary>
    /// <param name="scenePath">The scene file's path.</param>
    /// <returns>The settings.</returns>
    /// <remarks>
    ///     ⚠ <b>A missing or unreadable file is the defaults rather than an error.</b> Every scene
    ///     that existed before this format did has no sidecar, and an editor that refused to open one
    ///     would be an editor that cannot open any project made last week.
    /// </remarks>
    public static WorldSettings Load(string scenePath) {
        var path = PathFor(scenePath);

        if (!File.Exists(path)) {
            return new();
        }

        try {
            var settings = YamlSerializer.Parse<WorldSettings>(File.ReadAllText(path));

            return settings.Version <= Current ? settings : new();
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or YamlBindingException or YamlParseException) {
            return new();
        }
    }

    /// <summary>Writes a scene's settings beside it.</summary>
    /// <param name="scenePath">The scene file's path.</param>
    public void Save(string scenePath) {
        ArgumentException.ThrowIfNullOrEmpty(scenePath);

        var path = PathFor(scenePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));

        if (!string.IsNullOrEmpty(directory)) {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, YamlSerializer.ToYaml(this));
    }
}
