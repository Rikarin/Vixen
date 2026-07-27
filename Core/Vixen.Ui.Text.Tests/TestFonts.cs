// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;

namespace Vixen.Ui.Text.Tests;

/// <summary>The embedded test fonts, loaded once each.</summary>
/// <remarks>
///     These are the Consortium's fonts — see <c>Fonts/README.md</c> for whose they are. Loading is
///     cached because a face costs a parse and a native allocation, and four hundred conformance
///     cases share fourteen of them.
/// </remarks>
static class TestFonts {
    static readonly Lock Gate = new();
    static readonly Dictionary<string, FontFace> Loaded = new(StringComparer.Ordinal);

    /// <summary>The Nastaliq Arabic face — joining forms, cascading baselines, marks everywhere.</summary>
    public const string Arabic = "TestShapeAran.ttf";

    /// <summary>A Kannada face, for a script whose shaping reorders.</summary>
    public const string Kannada = "NotoSerifKannada-Regular.ttf";

    /// <summary>The face whose contextual alternate needs a space to be in the buffer.</summary>
    public const string ContextualLatin = "TestGSUBOne.otf";

    /// <summary>Loads one of the embedded fonts.</summary>
    /// <param name="name">Its file name.</param>
    /// <returns>The face. Owned by this class; do not dispose it.</returns>
    public static FontFace Load(string name) {
        lock (Gate) {
            if (Loaded.TryGetValue(name, out var cached)) {
                return cached;
            }

            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream($"Vixen.Ui.Text.Tests.Fonts.{name}")
                ?? throw new InvalidOperationException($"the font '{name}' is not embedded");

            using var memory = new MemoryStream();
            stream.CopyTo(memory);

            var face = FontFace.Load(memory.ToArray(), name: name);
            Loaded[name] = face;
            return face;
        }
    }
}
