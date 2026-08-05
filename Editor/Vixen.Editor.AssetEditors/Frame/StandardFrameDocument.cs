// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Editor.Assets.Compositors;
using Vixen.Editor.Core;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.PostFx;

namespace Vixen.Editor.AssetEditors.Frame;

/// <summary>A frame document open for editing as knobs rather than as a graph.</summary>
/// <remarks>
///     <para>
///         <b>Doc 39's sequencing step 3, and the file it opens is the same <c>.vxcompositor</c> a
///         hand-authored frame lives in.</b> There is exactly one document format and this is one
///         view over it: a document whose <c>game:</c> is a <c>!StandardFrame</c> gets the knobs, and
///         one that is a full graph is opened read-only with the resolved stacks still shown — which
///         is what makes the explode transition survivable rather than a cliff.
///     </para>
///     <para>
///         ⚠ <b>Written back through <see cref="CompositorWriter" />, and only for a document that
///         still has a frame node.</b> Reserialising an eleven-hundred-line hand-authored document
///         would reformat it and drop every comment in it — a save that silently rewrote sample 13's
///         frame is a worse outcome than a panel that declines to save. <see cref="CanEdit" /> is
///         that refusal, stated where the buttons can read it rather than discovered on the first
///         Ctrl+S.
///     </para>
///     <para>
///         ⚠ <b>Every write re-expands, and that is the whole live-apply story.</b>
///         <see cref="Expanded" /> is the document a builder would build — through
///         <c>PostEffectFactory</c>, the same transformer seam <c>CompositorBuilder.Build</c>
///         applies, never a second opinion of it — so the stage, target and buffer counts beside the
///         knobs move as the knobs move, and a guardrail refusal arrives on the edit that caused it
///         instead of at the next launch. <see cref="Changed" /> is what a viewport hosting a
///         <c>SceneRenderHost</c> subscribes to; <c>SceneRenderHost.Load</c> is documented as
///         callable again for exactly this.
///     </para>
/// </remarks>
public sealed class StandardFrameDocument : EditorDocument {
    /// <summary>What a frame document is written as.</summary>
    public const string Extension = ".vxcompositor";

    /// <summary>What the project's quality preset is called, beside the frame.</summary>
    public const string PresetFile = "RenderQuality.vxpreset";

    /// <summary>What an exploded file opens with, which is the CLI's sentence.</summary>
    /// <remarks>
    ///     Deliberately the same words <c>vixen frame explode</c> writes: two spellings of "this is
    ///     one-way" would be two claims a reader has to reconcile, and the CLI's is the one the
    ///     guide quotes.
    /// </remarks>
    public const string ExplodedHeader =
        "Exploded from !StandardFrame in the editor — one-way, deliberately. The knobs are gone, "
        + "every line below is yours to edit, and nothing regenerates this file.";

    /// <summary>Where the file is, absolute.</summary>
    public string AssetPath { get; }

    /// <summary>The document as it was read, with its frame node still on it.</summary>
    public GraphicsCompositorAsset Document { get; private set; } = new();

    /// <summary>The frame node, or null for a document that is a full graph.</summary>
    public StandardFrameAsset? Node => Document.Game as StandardFrameAsset;

    /// <summary>Whether the knobs apply, which is whether there is a frame node to turn.</summary>
    public bool CanEdit => Node is not null;

    /// <summary>The knobs, as the inspector edits them.</summary>
    public StandardFrameSettings Settings { get; } = new();

    /// <summary>The look profile the document carries inline, as the inspector edits it.</summary>
    public LookSettings Look { get; } = new();

    /// <summary>The project's <c>RenderQuality.vxpreset</c>, or null where it has none.</summary>
    public RenderQualityAsset? Preset { get; private set; }

    /// <summary>What a builder would build from this document — the expansion, live.</summary>
    public GraphicsCompositorAsset Expanded { get; private set; } = new();

    /// <summary>What reading or expanding it had to say, guardrail refusals included.</summary>
    public IReadOnlyList<string> Diagnostics { get; private set; } = [];

    /// <summary>The tier the document resolves against, with the host's pick standing in.</summary>
    /// <remarks>
    ///     ⚠ <b>The host's pick is <c>QualityTier.High</c> here because a file on disk has no
    ///     host.</b> It matches <c>CompositorBuilder.Quality</c>'s own default, which is what a
    ///     document that declines actually gets from an unconfigured host — so the panel shows the
    ///     ordinary case rather than an invented one, and says which reading it is showing.
    /// </remarks>
    public QualityTier Tier => Settings.Tier ?? QualityTier.High;

    /// <summary>Whether the tier came from the document or from the host standing in for it.</summary>
    public bool TierIsHosts => Settings.Tier is null;

    /// <summary>Raised whenever the knobs, the look or the file underneath them changed.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The live-apply seam.</b> Everything derived — the expansion, the resolved quality
    ///         stack, the diagnostics — is already rebuilt by the time this runs, so a subscriber
    ///         reads properties rather than being handed a payload it has to unpack.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It fires on a reload from disk too, and that is the point of putting it here
    ///         rather than on the view.</b> A frame edited in a text editor beside the running editor
    ///         is the case doc 39's hot-reload sentence is about; the panel and a viewport should not
    ///         need two different subscriptions to notice it.
    ///     </para>
    /// </remarks>
    public event Action<StandardFrameDocument>? Changed;

    /// <summary>Opens a frame document.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    /// <exception cref="ArgumentException"><paramref name="path" /> is null or empty.</exception>
    public StandardFrameDocument(EditorProject project, AssetId asset, string path)
        : base(project, asset, Path.GetFileName(path)) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        AssetPath = path;
        Reload();
    }

    /// <summary>Reads the file again, from disk, and rebuilds everything derived from it.</summary>
    /// <remarks>
    ///     ⚠ <b>It does not touch the undo stack, and it is not an undoable edit.</b> What it
    ///     replaces is the file's own contents; a stack whose entries described the previous file
    ///     would undo edits into a document that no longer has the members they name.
    /// </remarks>
    public void Reload() {
        var complaints = new List<string>();

        try {
            var text = AssetFile.Read(AssetPath);

            Document = text.Trim().Length == 0
                ? new GraphicsCompositorAsset { Version = CompositorBuilder.SupportedVersion }
                : YamlSerializer.Parse<GraphicsCompositorAsset>(text);
        } catch (Exception failure) when (failure is YamlParseException or YamlBindingException) {
            Document = new() { Version = CompositorBuilder.SupportedVersion };
            complaints.Add(failure.Message);
        }

        if (Node is { } node) {
            Settings.Read(node);
        }

        Look.Read(Node?.Look);
        Preset = ReadPreset(complaints);

        Restate(complaints);
    }

    /// <summary>Pushes what the inspector wrote back onto the node and re-expands.</summary>
    /// <returns>Whether there was a frame node to write to.</returns>
    /// <remarks>
    ///     ⚠ <b>Whole-node rather than per-member, which is what keeps a <c>record</c> editable at
    ///     all.</b> The mirrors are the only mutable copy; each carries forward everything it does
    ///     not model, so applying twice is the same as applying once and nothing the panel cannot
    ///     draw is lost on the way through.
    /// </remarks>
    public bool Apply() {
        if (Node is not { } node) {
            return false;
        }

        Document = Document with { Game = Settings.ToAsset(node) with { Look = Look.ToAsset() } };

        Restate([]);
        return true;
    }

    /// <summary>Whether exploding would do anything, which is whether there is a node to expand.</summary>
    public bool CanExplode => Node is not null;

    /// <summary>
    ///     Replaces the <c>!StandardFrame</c> node with the graph it stood for, in place, keeping a
    ///     copy of what was there.
    /// </summary>
    /// <returns>Where the copy of the authored document was put.</returns>
    /// <exception cref="InvalidOperationException">There is no frame node, or the expansion refused.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>One-way, and the file says so at the top</b> — the CLI's contract, unchanged. What
    ///         the editor adds is the one thing a button needs and a command line does not: the
    ///         gesture is one click, so the authored seven lines are copied to
    ///         <c>&lt;name&gt;.vxcompositor.authored</c> first. Doc 39 calls explode "one-way,
    ///         clearly marked"; it does not ask for it to be unrecoverable, and a person who
    ///         explodes to look at the output and then wants their knobs back should not have to
    ///         reach for version control.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The document is re-read afterwards, so <see cref="Node" /> becomes null and the
    ///         panel stops claiming this is a Standard Frame.</b> An inspector still showing knobs
    ///         over a file that no longer has them would be a form whose every write was discarded —
    ///         which is exactly the silent kind of destructive this method exists not to be.
    ///     </para>
    /// </remarks>
    public string Explode() {
        if (Node is null) {
            throw new InvalidOperationException(
                $"'{Path.GetFileName(AssetPath)}' contains no !StandardFrame, so there is nothing to "
                + "explode — it is already the expanded form."
            );
        }

        // Applied first, because exploding a document while the panel holds unsaved knobs would
        // expand the knobs on disk rather than the ones on screen — and the difference is invisible
        // until somebody notices the shadows are wrong.
        Apply();

        var exploded = PostEffectFactory.Transform(Document, out var notes);

        if (ReferenceEquals(exploded, Document)) {
            throw new InvalidOperationException("The expansion returned the document unchanged.");
        }

        var kept = AssetPath + ".authored";

        AssetFile.Write(kept, YamlSerializer.ToYaml(Document));
        AssetFile.Write(AssetPath, CompositorWriter.Write(exploded, notes, ExplodedHeader));

        Reload();
        Stack.MarkClean();

        return kept;
    }

    /// <summary>The document as this document would write it, without writing it.</summary>
    /// <returns>The YAML.</returns>
    public string ToYaml() => CompositorWriter.Write(Document);

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The document is a full graph rather than a frame node.</exception>
    protected override void SaveCore() {
        if (!CanEdit) {
            throw new InvalidOperationException(
                $"'{Path.GetFileName(AssetPath)}' is a hand-authored document. Saving it from the "
                + "frame panel would rewrite every line of it and drop its comments, so the panel "
                + "reads it and does not write it."
            );
        }

        AssetFile.Write(AssetPath, ToYaml());
    }

    /// <summary>Rebuilds the expansion and the diagnostics, then says so.</summary>
    void Restate(List<string> complaints) {
        try {
            Expanded = PostEffectFactory.Transform(Document, out _);
        } catch (InvalidOperationException refusal) {
            // The guardrails doc 39's audit paid for, arriving on the edit that caused them. The
            // expansion's own refusals name the problem; repeating them would say it worse.
            Expanded = Document;
            complaints.Add(refusal.Message);
        }

        Diagnostics = complaints;
        Changed?.Invoke(this);
    }

    /// <summary>Reads the project's quality preset, which is the waterfall's middle layer.</summary>
    /// <remarks>
    ///     ⚠ <b>By convention rather than by reference, because the document has no way to name
    ///     it.</b> A host hands the preset to <c>PostEffectFactory.Preset</c> in code; nothing in the
    ///     frame file points at it. <c>Assets/RenderQuality.vxpreset</c> is the path the game
    ///     template stamps out and the guide documents, so it is the one the panel looks in — and a
    ///     project that loads its preset from somewhere else sees the engine defaults here, which
    ///     the panel says out loud rather than implying.
    /// </remarks>
    RenderQualityAsset? ReadPreset(List<string> complaints) {
        var path = Path.Combine(Project.Paths.Assets, PresetFile);

        if (!File.Exists(path)) {
            return null;
        }

        try {
            var text = AssetFile.Read(path);
            return text.Trim().Length == 0 ? null : YamlSerializer.Parse<RenderQualityAsset>(text);
        } catch (Exception failure) when (failure is YamlParseException or YamlBindingException) {
            complaints.Add($"{PresetFile}: {failure.Message}");
            return null;
        }
    }
}
