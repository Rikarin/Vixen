// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Inspector;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.PostFx;

namespace Vixen.Editor.AssetEditors.Frame;

/// <summary>Which tier a frame document asks for, including the reading where it declines to.</summary>
/// <remarks>
///     ⚠ <b>A fifth member rather than a nullable enum, and the reason is the drawer table.</b>
///     <c>StandardFrameAsset.Quality</c> is <c>QualityTier?</c> because a document that writes
///     nothing has left the decision to <c>GraphicsOptions.Quality</c> — a real and load-bearing
///     reading, not an accident. <c>OptionalDrawer</c> is registered for the nullable scalars and
///     not for nullable enums, so a <c>QualityTier?</c> member would fall through to the read-only
///     last resort and the most important knob on the node would be grey text. Naming the empty
///     reading is also better copy than an unticked box: <see cref="Host" /> says what actually
///     happens.
/// </remarks>
public enum FrameQualityChoice {
    /// <summary>The document declines, and whoever launched the game decides.</summary>
    Host,

    /// <summary>Low.</summary>
    Low,

    /// <summary>Medium.</summary>
    Medium,

    /// <summary>High.</summary>
    High,

    /// <summary>Epic.</summary>
    Epic
}

/// <summary>The Standard Frame's knobs, as something an inspector can write to.</summary>
/// <remarks>
///     <para>
///         <b>A mutable mirror beside the immutable node, on <c>TerrainBrushSettings</c>'s
///         reasoning.</b> <see cref="StandardFrameAsset" /> is a <c>record</c> with <c>init</c>
///         members because a document is a value and the expansion has to be a pure function of it;
///         the editing pipeline writes through property setters. So this is what the panel edits,
///         and <see cref="ToAsset" /> is what the document is rebuilt from after each write.
///     </para>
///     <para>
///         ⚠ <b>It carries only the knobs, and deliberately not <c>Preset</c>, <c>Look</c> or
///         <c>Extensions</c>.</b> Those three are whole assets and node lists inline in the
///         document; round-tripping them through a flat mirror is how an editor quietly drops the
///         half of a file it did not model. <see cref="ToAsset" /> is a <c>with</c> over the node
///         the document read, so everything this type says nothing about survives untouched — which
///         is the same "says nothing" discipline the volume settings are built on.
///     </para>
/// </remarks>
[DataContract("StandardFrameSettings")]
public sealed class StandardFrameSettings {
    /// <summary>The scalability tier the numeric sub-knobs are read from.</summary>
    [Inspector]
    [Tooltip(
        "Which column of the quality table the frame resolves against. Host leaves it to "
        + "GraphicsOptions.Quality, which is what a settings screen switches without editing this file."
    )]
    public FrameQualityChoice Quality { get; set; } = FrameQualityChoice.Host;

    /// <summary>Sun and lamp shadows.</summary>
    [Inspector]
    [Tooltip("Off, cascades for the sun with an atlas for the lamps, or cascades plus the virtual shadow map.")]
    public ShadowMode Shadows { get; set; } = ShadowMode.Cascades;

    /// <summary>The global-illumination stack.</summary>
    [Inspector]
    [Tooltip(
        "Off is direct light only. Ambient adds the occlusion pair. Probes is doc 19's whole stack, "
        + "and needs the host's shading permutations before it does more than nothing."
    )]
    public GiMode Gi { get; set; } = GiMode.Off;

    /// <summary>Reflections.</summary>
    [Inspector]
    [Tooltip("Screen traces the frame it already has. Probe is reserved and emits nothing yet.")]
    public ReflectionsMode Reflections { get; set; } = ReflectionsMode.Off;

    /// <summary>Antialiasing — and, through it, whether the frame has motion vectors.</summary>
    [Inspector]
    [Tooltip("Taa and TaaFxaa emit the velocity pass and the Motion stage; Off and Fxaa do not.")]
    public AntialiasingMode Antialiasing { get; set; } = AntialiasingMode.Fxaa;

    /// <summary>Whether the frame meters itself or trusts the camera.</summary>
    [Inspector]
    [Tooltip("Automatic runs the histogram meter and the tonemap reads its buffer.")]
    public ExposureMode Exposure { get; set; } = ExposureMode.Fixed;

    /// <summary>Whether the additive particle pass and its stage are emitted.</summary>
    [Inspector]
    [Tooltip("The stage has to exist before a ParticleStage can name it, so this is on by default.")]
    public bool Particles { get; set; } = true;

    /// <summary>The resource the finished frame is written to.</summary>
    [Inspector]
    [Tooltip("Declared by the expansion and importable by the host — an import of the same name wins.")]
    public string Output { get; set; } = "SceneColour";

    /// <summary>Reads a node's knobs into the mirror.</summary>
    /// <param name="asset">The node.</param>
    /// <exception cref="ArgumentNullException"><paramref name="asset" /> is null.</exception>
    public void Read(StandardFrameAsset asset) {
        ArgumentNullException.ThrowIfNull(asset);

        Quality = asset.Quality switch {
            QualityTier.Low => FrameQualityChoice.Low,
            QualityTier.Medium => FrameQualityChoice.Medium,
            QualityTier.High => FrameQualityChoice.High,
            QualityTier.Epic => FrameQualityChoice.Epic,
            _ => FrameQualityChoice.Host
        };

        Shadows = asset.Shadows;
        Gi = asset.Gi;
        Reflections = asset.Reflections;
        Antialiasing = asset.Antialiasing;
        Exposure = asset.Exposure;
        Particles = asset.Particles;
        Output = asset.Output;
    }

    /// <summary>The node with this mirror's knobs on it, and everything else left as it was.</summary>
    /// <param name="asset">The node the document holds.</param>
    /// <returns>A new node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="asset" /> is null.</exception>
    public StandardFrameAsset ToAsset(StandardFrameAsset asset) {
        ArgumentNullException.ThrowIfNull(asset);

        return asset with {
            Quality = Tier,
            Shadows = Shadows,
            Gi = Gi,
            Reflections = Reflections,
            Antialiasing = Antialiasing,
            Exposure = Exposure,
            Particles = Particles,

            // An empty box is the node's own default rather than an empty resource name: a document
            // that wrote `output: ""` declares a target nothing can be written to, and the expansion
            // would refuse it with a code the person who cleared the box has no way to connect to
            // the box.
            Output = Output is { Length: > 0 } named ? named : "SceneColour"
        };
    }

    /// <summary>The tier this mirror asks for, or null where it declines.</summary>
    public QualityTier? Tier => Quality switch {
        FrameQualityChoice.Low => QualityTier.Low,
        FrameQualityChoice.Medium => QualityTier.Medium,
        FrameQualityChoice.High => QualityTier.High,
        FrameQualityChoice.Epic => QualityTier.Epic,
        _ => null
    };
}
