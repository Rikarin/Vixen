// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Vixen.Texturing.Painting;

namespace Vixen.Texturing.Layers;

/// <summary>Fills the externals of a compiled stack that are not painted pixels.</summary>
/// <param name="uploads">Where to put them.</param>
/// <param name="plan">The plan.</param>
/// <param name="owed">The images owed.</param>
/// <returns>Why any could not be read.</returns>
public delegate IReadOnlyList<string> LayerStackPictures(
    TextureUploads uploads,
    TexturePlan plan,
    IReadOnlyList<TextureGraphExternal> owed
);

/// <summary>A layer stack through the one evaluator, with no editor and no panel.</summary>
/// <remarks>
///     <para>
///         <b>Plan § 3.2: a stack compiles to a graph.</b> This is the whole of what a host needs to
///         show one, with or without an editor, and everything it adds to the plan is the
///         plumbing of painted pixels: <c>vxpaint:</c> references become uploaded images, watched
///         by a <see cref="PaintTileCache" />. <c>LayerStackPreview</c> is built on this.
///     </para>
///     <para>
///         ⚠ <b>There is no composite anywhere else.</b> What is under the brush in a host built on
///         this is what the plan produced, for every operator a layer has, and Vixen's own panes are
///         such a host. A device-free host shows the painted layer's pixels and says so; it does not
///         get a second opinion about the arithmetic.
///     </para>
///     <para>
///         ⚠ <b>Not thread-safe, and it holds a frame's worth of device state.</b> One thread, the
///         one that owns the device.
///     </para>
/// </remarks>
public sealed class LayerStackEvaluator : IDisposable {
    readonly TexturePlanEvaluator evaluator;
    readonly PaintCanvasStore canvases;
    readonly string folder;
    readonly LayerStackPictures? pictures;
    readonly PaintTileCache tiles = new();
    TextureGraphCompiler? compiler;
    NodeTypeRegistry? compiledFor;
    TextureUploads? uploads;
    ImmutableArray<TextureResultKey> previous;

    /// <summary>An evaluator for one stack file.</summary>
    /// <param name="evaluator">The plan evaluator.</param>
    /// <param name="canvases">The open canvases, which strokes write into.</param>
    /// <param name="stackPath">The stack's file, which its paint files are relative to.</param>
    /// <param name="pictures">Fills whatever else the plan reads, or nothing.</param>
    public LayerStackEvaluator(
        TexturePlanEvaluator evaluator,
        PaintCanvasStore canvases,
        string stackPath,
        LayerStackPictures? pictures = null
    ) {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(canvases);
        ArgumentException.ThrowIfNullOrWhiteSpace(stackPath);

        this.evaluator = evaluator;
        this.canvases = canvases;
        this.pictures = pictures;

        folder = Path.GetDirectoryName(Path.GetFullPath(stackPath)) ?? "";
    }

    /// <summary>The plan the last <see cref="Prepare" /> left, or nothing when it failed.</summary>
    public TexturePlan? Plan { get; private set; }

    /// <summary>What each channel's map is written to.</summary>
    public ImmutableArray<TextureGraphOutput> Outputs { get; private set; } = [];

    /// <summary>What crosses to the device, for a host that wants to show it.</summary>
    public PaintTileCache Tiles => tiles;

    /// <summary>Whether the last <see cref="Prepare" /> compiled, rather than only moving values.</summary>
    public bool Compiled { get; private set; }

    /// <summary>What building the graph said on the last <see cref="Prepare" />, about layers.</summary>
    public ImmutableArray<LayerStackProblem> Problems { get; private set; } = [];

    /// <summary>What the last compilation said, about nodes.</summary>
    /// <remarks>Kept across a <see cref="Prepare" /> that only moved values: a refresh never speaks.</remarks>
    public ImmutableArray<NodeDiagnostic> Diagnostics { get; private set; } = [];

    /// <summary>Which layer emitted each node.</summary>
    public ImmutableDictionary<NodeId, string> Layers { get; private set; } =
        ImmutableDictionary<NodeId, string>.Empty;

    /// <summary>Brings the plan up to a stack, compiling only for more than a value (plan § 3.2).</summary>
    /// <param name="stack">The stack.</param>
    /// <param name="set">The texture set.</param>
    /// <param name="registry">The node types it can use.</param>
    /// <param name="subGraphs">Where compounds come from.</param>
    /// <returns>Why it cannot evaluate, or nothing.</returns>
    public IReadOnlyList<string> Prepare(
        LayerStackAsset stack,
        TextureSetAsset set,
        NodeTypeRegistry registry,
        ISubGraphSource? subGraphs
    ) {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(registry);

        var build = LayerStackGraph.Build(stack, set, registry);

        Problems = build.Problems;
        Layers = build.Layers;

        if (build.HasErrors) {
            Discard();
            Diagnostics = [];

            return [.. build.Problems.Select(static problem => problem.Message)];
        }

        Compiled = false;

        // ⚠ `rebuilt`, because `Build` makes a new graph every time, and the compiler's own check on
        // anything the graph does not say. Its size, its library and its seed came from this stack
        // once, so a compiler left to answer would answer for the stack it was built for.
        if (Current(stack, registry, subGraphs) && compiler!.Refresh(build.Graph, rebuilt: true) is { } refreshed) {
            Plan = refreshed;

            return [];
        }

        Compiled = true;
        Discard();

        compiledFor = registry;
        compiler = new(registry) {
            BaseWidth = stack.BaseWidth,
            BaseHeight = stack.BaseHeight,
            Seed = stack.Seed,
            SubGraphSource = subGraphs
        };

        var compilation = compiler.Compile(build.Graph);

        Diagnostics = compilation.Diagnostics;

        if (compilation.Artefact is not { } plan) {
            return [.. compilation.Diagnostics.Select(static diagnostic => diagnostic.Message)];
        }

        var refusals = Upload(plan, compiler.Externals);

        if (refusals.Count > 0) {
            Discard();

            return refusals;
        }

        Plan = plan;
        Outputs = compiler.Outputs;

        return [];
    }

    /// <summary>Evaluates what changed since the last evaluation.</summary>
    /// <param name="plan">The plan or an extension of it, or nothing for the plan itself.</param>
    /// <returns>The bake, which the caller disposes.</returns>
    /// <exception cref="InvalidOperationException">Nothing has been prepared.</exception>
    public TextureBake Evaluate(TexturePlan? plan = null) {
        if (Plan is not { } current || uploads is null) {
            throw new InvalidOperationException("This stack has no plan, so there is nothing to evaluate.");
        }

        var wanted = plan ?? current;
        var changed = tiles.Sync(uploads);
        var regions = previous.IsDefault ? null : new TextureRegions(Fitted(previous, wanted.Images.Length), changed);
        var bake = evaluator.EvaluateIncremental(wanted, uploads.Externals, regions);

        previous = bake.Keys;

        return bake;
    }

    /// <summary>Evaluates from nothing, sharing no result with any evaluation before it.</summary>
    /// <returns>The bake, which the caller disposes.</returns>
    /// <remarks>
    ///     ⚠ <b>What a stroke's repair is checked against, so it must not be able to see one.</b> It
    ///     runs every op of the whole plan and reads no cache, which is what an export owes as well.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Nothing has been prepared.</exception>
    public TextureBake EvaluateClean() {
        if (Plan is not { } current || uploads is null) {
            throw new InvalidOperationException("This stack has no plan, so there is nothing to evaluate.");
        }

        tiles.Sync(uploads);

        return evaluator.Evaluate(current, uploads.Externals);
    }

    /// <inheritdoc />
    public void Dispose() => Discard();

    /// <summary>The last keys, as long as this plan's images.</summary>
    /// <remarks>
    ///     ⚠ <b>An extension keeps the plan's images and adds to the end, so the part they share still says
    ///     what changed.</b> Giving up on any difference in length made the stamp after a thumbnail run the
    ///     whole stack, once per stroke. An image that is new has no key, and so has nothing to repair.
    /// </remarks>
    static ImmutableArray<TextureResultKey> Fitted(ImmutableArray<TextureResultKey> keys, int images) {
        if (keys.Length == images) {
            return keys;
        }

        var fitted = new TextureResultKey[images];

        keys.AsSpan(0, Math.Min(keys.Length, images)).CopyTo(fitted);

        return [.. fitted];
    }

    /// <summary>Whether what is compiled was compiled for this stack's size, seed and library.</summary>
    bool Current(LayerStackAsset stack, NodeTypeRegistry registry, ISubGraphSource? subGraphs) {
        if (uploads is null
            || compiler is not { } held
            || held.BaseWidth != stack.BaseWidth
            || held.BaseHeight != stack.BaseHeight
            || !ReferenceEquals(compiledFor, registry)
            || !ReferenceEquals(held.SubGraphSource, subGraphs)) {
            return false;
        }

        // A number like any other, and the one a refresh can move without the graph saying so.
        held.Seed = stack.Seed;

        return true;
    }

    void Discard() {
        Plan = null;
        Outputs = [];
        previous = default;
        tiles.Clear();
        uploads?.Dispose();
        uploads = null;
        compiler = null;
    }

    List<string> Upload(TexturePlan plan, ImmutableArray<TextureGraphExternal> externals) {
        uploads = new(evaluator.Device);

        var owed = TextureGraphExternals.Upload(uploads, plan, externals);
        List<TextureGraphExternal> elsewhere = [];
        List<string> refusals = [];
        Dictionary<(int Width, int Height), byte[]> blanks = [];

        foreach (var entry in owed) {
            if (!PaintReference.Claims(entry.Asset)) {
                elsewhere.Add(entry);
            } else if (Painted(plan, entry, blanks) is { } refusal) {
                refusals.Add(refusal);
            }
        }

        if (elsewhere.Count > 0) {
            if (pictures is null) {
                refusals.Add($"Nothing here can read '{elsewhere[0].Asset}'.");
            } else {
                refusals.AddRange(pictures(uploads, plan, elsewhere));
            }
        }

        return refusals;
    }

    string? Painted(
        TexturePlan plan,
        TextureGraphExternal entry,
        Dictionary<(int Width, int Height), byte[]> blanks
    ) {
        if (!PaintReference.TryParse(entry.Asset, out var relative, out var usage)) {
            return $"'{entry.Asset}' claims to be painted pixels and does not name both a file and a channel.";
        }

        var file = Path.GetFullPath(Path.Combine(folder, relative));
        PaintCanvas? canvas;

        try {
            canvas = canvases.Open(file);
        } catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException) {
            return $"'{relative}' would not read: {failure.Message}";
        }

        if (canvas is null) {
            return $"'{relative}' is the painted canvas this layer names and there is no such file beside the "
                + "stack, so its pixels cannot be read.";
        }

        var image = canvas.Has(usage) ? canvas.Channel(usage) : null;
        var width = canvas.Width;
        var height = canvas.Height;
        ReadOnlySpan<byte> texels;

        // ⚠ One blank per size. A 4K stack of unpainted channels would otherwise allocate 67 MB for
        // each of them to say the same thing.
        if (image is null) {
            if (!blanks.TryGetValue((width, height), out var blank)) {
                blanks[(width, height)] = blank = new byte[(long)width * height * PaintImage.BytesPerTexel];
            }

            texels = blank;
        } else {
            texels = image.ToTexels();
        }

        try {
            uploads!.Add(plan, entry.Image, width, height, texels, new(image?.Digest ?? 1));
        } catch (ArgumentException failure) {
            return $"'{relative}' could not be uploaded: {failure.Message}";
        }

        tiles.Watch(entry.Image, () => canvases.Open(file) is { } open && open.Has(usage) ? open.Channel(usage) : null);

        return null;
    }
}
