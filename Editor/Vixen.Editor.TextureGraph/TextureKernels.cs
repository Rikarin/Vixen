// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;

namespace Vixen.Editor.TextureGraph;

/// <summary>The Raven sources under <c>Shaders/</c>, and the format variants a plan needs of them.</summary>
/// <remarks>
///     <para>
///         <b>Embedded rather than compiled to a committed <c>.spv</c>, and the reason is a property
///         of the target rather than a preference.</b> A storage image's texel format is part of its
///         <em>type</em> — SPIR-V puts it in <c>OpTypeImage</c>, GLSL in the layout qualifier — and
///         Raven's <c>[Permutation]</c> values are bool, int and uint, so a format cannot be one. A
///         kernel that writes <c>rgba8</c> in one plan and <c>rgba16f</c> in the next is therefore
///         two modules, and there is no spelling of the source that makes it one.
///     </para>
///     <para>
///         ⚠ <b>So the variant is produced by rewriting the one <c>[Format(…)]</c> the source
///         carries</b>, which is a shader variant in the plainest sense and is named as one rather
///         than hidden. <see cref="Variant(string,TextureFormat)" /> refuses a source that does not
///         carry exactly one, so a kernel that grew a second storage image is a failure here rather
///         than a silent rewrite of
///         whichever came first.
///     </para>
///     <para>
///         <b>What this arrangement gives up, and what it buys.</b> It gives up
///         <c>CheckShaders</c>' editor half: that gate proves a committed module matches the source
///         beside it, and there is no committed module to be stale. What replaces it is stronger and
///         runs on every machine — <c>TextureKernelTests</c> puts every kernel through the real Raven
///         front end in every format a plan can ask for, with no device, so a kernel that does not
///         compile fails a test that never skips. A gate that can only run where there is a GPU is
///         a gate that reports success on the day it does not run.
///     </para>
/// </remarks>
public static class TextureKernels {
    const string Prefix = "Vixen.Editor.TextureGraph.Shaders.";
    const string FormatMarker = "[Format(\"";

    static readonly ImmutableDictionary<string, string> Sources = Read();

    /// <summary>Every kernel this assembly ships, by the shader name a <see cref="TextureOp" /> gives.</summary>
    public static IReadOnlyList<string> Names { get; } =
        [.. Sources.Keys.OrderBy(name => name, StringComparer.Ordinal)];

    /// <summary>The source of one kernel, exactly as committed.</summary>
    /// <param name="kernel">The shader name.</param>
    /// <returns>The Raven text.</returns>
    /// <exception cref="ArgumentException">No kernel by that name is embedded.</exception>
    public static string Source(string kernel) =>
        Sources.TryGetValue(kernel, out var source)
            ? source
            : throw new ArgumentException(
                $"No kernel called '{kernel}' is embedded in this assembly. It ships {string.Join(", ", Names)}.",
                nameof(kernel)
            );

    /// <summary>The source of one kernel, writing into an image of a given format.</summary>
    /// <param name="kernel">The shader name.</param>
    /// <param name="output">What the image it writes stores.</param>
    /// <returns>The Raven text to compile.</returns>
    /// <exception cref="ArgumentException">No such kernel, or its source does not declare one storage image.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The format is one no kernel can write.</exception>
    public static string Variant(string kernel, TextureFormat output) => Variant(kernel, Source(kernel), output);

    /// <summary>The same rewrite over a source this assembly did not ship.</summary>
    /// <param name="kernel">The shader name, used only in the messages.</param>
    /// <param name="source">The Raven text.</param>
    /// <param name="output">What the image it writes stores.</param>
    /// <returns>The Raven text to compile.</returns>
    /// <exception cref="ArgumentException">The source does not declare exactly one storage image.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The format is one no kernel can write.</exception>
    /// <remarks>
    ///     ⚠ <b>The half <a href="https://github.com/Rikarin/Vixen/issues/729">#729</a> needed, and
    ///     it is the whole of what an authored kernel asks of this class.</b> A plan may carry a
    ///     kernel a graph wrote — doc 48 § D6's Pixel Processor — and such a source is not an
    ///     embedded resource and never will be; what it is is text, which needs exactly the same
    ///     format rewrite for exactly the same reason. Reading and rewriting are two questions and
    ///     this is the second one alone.
    /// </remarks>
    public static string Variant(string kernel, string source, TextureFormat output) {
        ArgumentNullException.ThrowIfNull(source);

        var wanted = TextureFormats.RavenName(output);
        var at = source.IndexOf(FormatMarker, StringComparison.Ordinal);

        if (at < 0) {
            throw new ArgumentException(
                $"'{kernel}' declares no storage image, so there is nothing for it to write.",
                nameof(kernel)
            );
        }

        if (source.IndexOf(FormatMarker, at + FormatMarker.Length, StringComparison.Ordinal) >= 0) {
            throw new ArgumentException(
                $"'{kernel}' declares more than one storage image. A kernel writes exactly one image, because "
                + "the plan's op writes exactly one — and which of two a variant was built for could not be said.",
                nameof(kernel)
            );
        }

        var start = at + FormatMarker.Length;
        var end = source.IndexOf('"', start);

        if (end < 0) {
            throw new ArgumentException($"'{kernel}' has an unterminated [Format(\"…\")].", nameof(kernel));
        }

        return string.Concat(source.AsSpan(0, start), wanted, source.AsSpan(end));
    }

    /// <summary>What the compiler is told the variant's file is called, so a diagnostic points at it.</summary>
    /// <param name="kernel">The shader name.</param>
    /// <param name="output">The format the variant writes.</param>
    /// <returns>A name that looks like a file even though it is not one.</returns>
    public static string VariantName(string kernel, TextureFormat output) =>
        string.Create(CultureInfo.InvariantCulture, $"{kernel}.{TextureFormats.RavenName(output)}.rvn");

    static ImmutableDictionary<string, string> Read() {
        var assembly = typeof(TextureKernels).Assembly;
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        foreach (var resource in assembly.GetManifestResourceNames()) {
            if (!resource.StartsWith(Prefix, StringComparison.Ordinal)
                || !resource.EndsWith(".rvn", StringComparison.Ordinal)) {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resource);

            if (stream is null) {
                continue;
            }

            using var reader = new StreamReader(stream);

            builder[resource[Prefix.Length..^".rvn".Length]] = reader.ReadToEnd();
        }

        return builder.ToImmutable();
    }
}
