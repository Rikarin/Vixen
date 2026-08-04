// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Yaml;
using Vixen.Editor.Assets.Compositors;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.PostFx;

namespace Vixen.Cli;

/// <summary>`vixen frame explode` — docs/plan/39 § 5's escape hatch.</summary>
/// <remarks>
///     <para>
///         One-way on purpose, and the file says so at the top: the expansion replaces the
///         <c>!StandardFrame</c> node with the graph it stood for, comments included, and from then
///         on the document is hand-authored like any other. The knobs are gone; that is what
///         "eject" means everywhere else, and pretending the file could be folded back would be
///         pretending edits to it could survive the folding.
///     </para>
///     <para>
///         The expansion is reached through <see cref="PostEffectFactory" /> — the same transformer
///         seam <c>CompositorBuilder.Build</c> applies — so what this writes is precisely what the
///         builder would have built, never a second opinion of it.
///     </para>
/// </remarks>
internal static class FrameRunner {
    /// <summary>What an exploded file opens with.</summary>
    const string Header =
        "Exploded from !StandardFrame by `vixen frame explode` — one-way, deliberately. The knobs are "
        + "gone, every line below is yours to edit, and nothing regenerates this file.";

    /// <summary>Expands a document's <c>!StandardFrame</c> and writes the result as text.</summary>
    /// <param name="path">The <c>.vxcompositor</c> to explode.</param>
    /// <param name="inPlace">
    ///     Whether to overwrite the document itself rather than writing
    ///     <c>&lt;name&gt;.exploded.vxcompositor</c> beside it.
    /// </param>
    /// <param name="output">Where the summary goes.</param>
    /// <param name="error">Where complaints go.</param>
    public static ExitCode Explode(string path, bool inPlace, TextWriter output, TextWriter error) {
        if (!File.Exists(path)) {
            error.WriteLine($"There is no document at '{path}'.");
            return ExitCode.UsageError;
        }

        GraphicsCompositorAsset document;

        try {
            document = YamlSerializer.Parse<GraphicsCompositorAsset>(File.ReadAllText(path));
        } catch (YamlParseException failure) {
            error.WriteLine($"'{path}' is not valid YAML: {failure.Message}");
            return ExitCode.Failed;
        } catch (YamlBindingException failure) {
            error.WriteLine($"'{path}' is not a compositor document: {failure.Message}");
            return ExitCode.Failed;
        }

        GraphicsCompositorAsset exploded;
        IReadOnlyDictionary<object, string> notes;

        try {
            exploded = PostEffectFactory.Transform(document, out notes);
        } catch (InvalidOperationException refusal) {
            // The expansion's own refusals — two frames, a frame inside a pass, a canonical name
            // redeclared differently — already name the problem; repeating them would say it worse.
            error.WriteLine(refusal.Message);
            return ExitCode.Failed;
        }

        if (ReferenceEquals(exploded, document)) {
            error.WriteLine(
                $"'{path}' contains no !StandardFrame, so there is nothing to explode — "
                + "it is already the expanded form."
            );

            return ExitCode.Failed;
        }

        var target = inPlace
            ? path
            : Path.Combine(
                Path.GetDirectoryName(path) ?? ".",
                Path.GetFileNameWithoutExtension(path) + ".exploded.vxcompositor"
            );

        File.WriteAllText(target, CompositorWriter.Write(exploded, notes, Header));

        var nodes = exploded.Game is SequenceAsset root ? root.Children.Length : 1;

        output.WriteLine(
            $"Exploded {nodes} node(s), {exploded.Resources.Length} resource(s) and "
            + $"{exploded.Stages.Length} stage(s) into {target}"
        );

        return ExitCode.Success;
    }
}
