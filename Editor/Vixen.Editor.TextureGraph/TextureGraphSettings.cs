// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.TextureGraph;

/// <summary>
///     What a texture graph declares about itself: doc 48 § D8's base resolution and § D5's seed.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>These were properties of the <em>compiler</em>, set by whichever host constructed
///         one, and a saved <c>.vxtexgraph</c> came back at whatever that host defaulted to —
///         <a href="https://github.com/Rikarin/Vixen/issues/719">#719</a>.</b> § D8 opens with "the
///         graph declares a base resolution" and the relative half was built; the declaring half had
///         nowhere to live, because <c>NodeGraphModel</c> carried a name, nodes, edges, groups,
///         comments and an interface. It carries a settings bag now, and this is the texture graph's
///         reading of it.
///     </para>
///     <para>
///         ⚠ <b>And a seed that is not in the file is a seed that changes between machines</b>, which
///         § D5 says plainly is not a source asset: "a procedural texture whose output changes
///         between runs". That was the sharper half of the same gap, because a resolution that came
///         back wrong is visible and a seed that came back different is just a different picture.
///     </para>
///     <para>
///         <b>What the graph declares and what the bake decides are two different lists, and only the
///         first is here.</b> <see cref="TextureGraphCompiler.BakeLevelOffset" /> is how big
///         <em>this run</em> is making the material, <c>Arguments</c> is what a
///         <c>.vxsmartmat</c> overrode, and <c>PreviewEveryNode</c> is what a panel wants — none of
///         them is a property of the graph, and putting any of them in the file would be a bake
///         somebody saved by accident.
///     </para>
/// </remarks>
public static class TextureGraphSettings {
    /// <summary>The key the authoring width is stored under.</summary>
    public const string BaseWidth = "baseWidth";

    /// <summary>The key the authoring height is stored under.</summary>
    public const string BaseHeight = "baseHeight";

    /// <summary>The key the seed is stored under.</summary>
    public const string Seed = "seed";

    /// <summary>Writes a graph's declarations into its own settings.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="width">The width it is authored at.</param>
    /// <param name="height">The height it is authored at.</param>
    /// <param name="seed">Its seed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="graph" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Either extent is not positive.</exception>
    public static void Declare(NodeGraphModel graph, int width, int height, uint seed) {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        graph.Settings[BaseWidth] = width.ToString(CultureInfo.InvariantCulture);
        graph.Settings[BaseHeight] = height.ToString(CultureInfo.InvariantCulture);
        graph.Settings[Seed] = seed.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Reads one of a graph's declared numbers, or the host's own value.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="key">Which setting.</param>
    /// <param name="fallback">What to use when the graph declares nothing, or nonsense.</param>
    /// <param name="problem">What was wrong with what it declared, or null.</param>
    /// <returns>The number to compile at.</returns>
    /// <remarks>
    ///     ⚠ <b>A graph that declares nothing keeps the host's value, and one that declares nonsense
    ///     keeps it <em>and says so</em>.</b> Answering with zero for an unparseable width would be a
    ///     plan whose every image is one texel — which validates, evaluates, and produces a material
    ///     nobody would connect with a hand edit to a file.
    /// </remarks>
    public static int Extent(NodeGraphModel graph, string key, int fallback, out string? problem) {
        ArgumentNullException.ThrowIfNull(graph);

        problem = null;

        var text = graph.SettingOf(key);

        if (text.Length == 0) {
            return fallback;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0) {
            problem = $"This graph declares {key} as '{text}', which is not a positive number of texels. "
                + $"It was compiled at {fallback} instead.";

            return fallback;
        }

        return value;
    }

    /// <summary>Reads a graph's declared seed, or the host's own.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="fallback">What to use when the graph declares nothing, or nonsense.</param>
    /// <param name="problem">What was wrong with what it declared, or null.</param>
    /// <returns>The seed to compile with.</returns>
    public static uint SeedOf(NodeGraphModel graph, uint fallback, out string? problem) {
        ArgumentNullException.ThrowIfNull(graph);

        problem = null;

        var text = graph.SettingOf(Seed);

        if (text.Length == 0) {
            return fallback;
        }

        if (!uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) {
            problem = $"This graph declares its seed as '{text}', which is not a number. It was compiled with "
                + $"{fallback} instead — so every noise in it is a different picture from the one that was saved.";

            return fallback;
        }

        return value;
    }
}
