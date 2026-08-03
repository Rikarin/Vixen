// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Animation.Constraints;

/// <summary>One step of a declared ground axis.</summary>
/// <remarks>Slope and height together, for the reason <see cref="GroundVariation" /> gives.</remarks>
[DataContract("HarnessGroundStep")]
public sealed class HarnessGroundStep {
    /// <summary>How steep, in degrees.</summary>
    public float Degrees { get; set; }

    /// <summary>How high, in metres.</summary>
    public float Height { get; set; }
}

/// <summary>One prop of a declared prop axis.</summary>
[DataContract("HarnessProp")]
public sealed class HarnessProp {
    /// <summary>What it is called, which is what the report shows.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Where it is.</summary>
    public Vector3 Position { get; set; }

    /// <summary>Which way it faces.</summary>
    public Quaternion Rotation { get; set; } = Quaternion.Identity;

    /// <summary>How big it is. What makes one prop a class of interchangeable ones.</summary>
    public Vector3 Scale { get; set; } = Vector3.One;
}

/// <summary>A declared prop axis: one binding slot, several things that could be in it.</summary>
[DataContract("HarnessPropAxis")]
public sealed class HarnessPropAxis {
    /// <summary>Which binding slot the goals reach it through.</summary>
    public string Slot { get; set; } = string.Empty;

    /// <summary>The props.</summary>
    public List<HarnessProp> Values { get; set; } = [];
}

/// <summary>What a run is allowed to get away with, as a file holds it.</summary>
[DataContract("HarnessThresholdRecord")]
public sealed class HarnessThresholdRecord {
    /// <summary>How far a goal may miss by, in metres. Zero leaves it unjudged.</summary>
    public float Residual { get; set; }

    /// <summary>How far into a surface a contact may sink, in metres. Zero leaves it unjudged.</summary>
    public float Penetration { get; set; }

    /// <summary>How hard an effector may change velocity, in m/s². Zero leaves it unjudged.</summary>
    public float Jerk { get; set; }

    /// <summary>Whether a chain running out of reach fails the build.</summary>
    public bool Reach { get; set; }

    /// <summary>What the harness reads.</summary>
    /// <returns>The thresholds.</returns>
    public HarnessThresholds Bake() =>
        new() { Residual = Residual, Penetration = Penetration, Jerk = Jerk, Reach = Reach };
}

/// <summary>A variation run, declared in the project rather than written in a test.</summary>
/// <remarks>
///     <para>
///         <b>The thresholds have to live somewhere a build and an editor both read.</b> Numbers
///         written into a test are numbers the person authoring the clip cannot see, cannot change
///         without a programmer, and will not believe. A <c>.vxharness</c> is reviewed with the
///         content it guards.
///     </para>
///     <para>
///         ⚠ <b>It names its assets by path and resolves none of them.</b> This assembly has no
///         importer and no project — <see cref="Resolve" /> is handed the loaded clip, rig and shapes
///         by whoever did have one. That is the same split every content type here makes, and it is
///         what lets the harness run in a test with no pipeline at all.
///     </para>
/// </remarks>
[DataContract("HarnessPlanContent")]
public sealed class HarnessPlanContent {
    /// <summary>The extension a project writes these under.</summary>
    public const string Extension = ".vxharness";

    /// <summary>The version this reader and writer speak.</summary>
    public const int Current = 1;

    /// <summary>Which version of the format this file is.</summary>
    public int Version { get; set; } = Current;

    /// <summary>What the run is called, for the report and the build log.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The clip being checked, by asset path.</summary>
    public string Clip { get; set; } = string.Empty;

    /// <summary>The rig it is played on, by asset path.</summary>
    public string Rig { get; set; } = string.Empty;

    /// <summary>The proxy shapes built against that rig, by asset path, or empty for none.</summary>
    public string Shapes { get; set; } = string.Empty;

    /// <summary>The priority ladder the clip's tags name, by asset path, or empty.</summary>
    public string Priorities { get; set; } = string.Empty;

    /// <summary>How many moments of the clip to watch.</summary>
    public int Samples { get; set; } = 32;

    /// <summary>The body sizes to try, where one is the rig as authored. Empty varies no body.</summary>
    public List<float> Bodies { get; set; } = [];

    /// <summary>The ground to try. Empty varies no ground.</summary>
    public List<HarnessGroundStep> Ground { get; set; } = [];

    /// <summary>The props to try. Empty varies no prop.</summary>
    public List<HarnessPropAxis> Props { get; set; } = [];

    /// <summary>What counts as a failure.</summary>
    public HarnessThresholdRecord Thresholds { get; set; } = new();

    /// <summary>How many configurations this declaration would run.</summary>
    /// <remarks>
    ///     ⚠ <b>The number an importer warns about.</b> Axes multiply: five bodies, four grounds and
    ///     three props is sixty configurations, and at thirty-two samples each that is a run somebody
    ///     started by accident.
    /// </remarks>
    public int Configurations {
        get {
            var total = Bodies.Count > 0 ? Bodies.Count : 1;

            if (Ground.Count > 0) {
                total *= Ground.Count;
            }

            foreach (var axis in Props) {
                if (axis.Values.Count > 0) {
                    total *= axis.Values.Count;
                }
            }

            return total;
        }
    }

    /// <summary>Turns the declaration into a run, given the things it named.</summary>
    /// <param name="skeleton">The rig it named.</param>
    /// <param name="clip">The clip it named, in its authored form.</param>
    /// <param name="shapes">The shape set it named, or <see langword="null" />.</param>
    /// <param name="ladder">The ladder it named, or <see langword="null" />.</param>
    /// <returns>The plan.</returns>
    public HarnessPlan Resolve(
        Skeleton skeleton,
        AnimationClipContent clip,
        ProxyShapeSet? shapes = null,
        PriorityLadder? ladder = null
    ) {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(clip);

        List<IVariationSource> axes = [];

        if (Bodies.Count > 0) {
            axes.Add(new BodyVariation(skeleton, [.. Bodies]));
        }

        if (Ground.Count > 0) {
            axes.Add(new GroundVariation([.. Ground.Select(static step => (step.Degrees, step.Height))]));
        }

        foreach (var axis in Props) {
            if (axis.Values.Count == 0 || axis.Slot.Length == 0) {
                continue;
            }

            axes.Add(
                new PropVariation(
                    axis.Slot,
                    [
                        .. axis.Values.Select(
                            static prop => (prop.Name, new BoneTransform(prop.Position, prop.Rotation, prop.Scale))
                        )
                    ]
                )
            );
        }

        return new() {
            Clip = clip,
            Skeleton = skeleton,
            Shapes = shapes,
            Ladder = ladder,
            Samples = Samples,
            Thresholds = Thresholds.Bake(),
            Variations = axes
        };
    }
}
