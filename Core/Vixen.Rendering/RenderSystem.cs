// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Threading;

namespace Vixen.Rendering;

/// <summary>
///     Drives one frame: extract, cull, prepare, sort.
/// </summary>
/// <remarks>
///     <para>
///         The phases of [docs/plan/06 § Frame structure], minus recording — which needs materials
///         and effects and is the next slice. What is here is the part that turns a scene into
///         ordered work lists, and it is entirely CPU-side and deterministic, which is why it can be
///         tested without a device.
///     </para>
///     <para>
///         <strong>The order is a data dependency, not a convention.</strong> Culling needs every
///         object extracted, because an object added late would be tested against a stale bitset;
///         preparation needs culling, because its whole value is doing per-visible-object work
///         rather than per-object work; sorting needs preparation, because a feature's sort group
///         may be something preparation resolved. Reordering any pair silently produces a frame
///         that is merely wrong rather than one that fails.
///     </para>
/// </remarks>
public sealed class RenderSystem : IDisposable {
    readonly List<RootRenderFeature> features = [];
    readonly List<RenderStage> stages = [];
    readonly List<RenderView> views = [];
    readonly Dictionary<(int View, int Stage), List<RenderNode>> nodes = [];
    bool disposed;

    /// <summary>Every renderable in the scene, and the per-feature arrays beside them.</summary>
    public RenderObjectStore Objects { get; } = new();

    /// <summary>Which objects each view can see.</summary>
    public VisibilityGroup Visibility { get; } = new();

    /// <summary>The scheduler culling and sorting run on. Null runs them inline.</summary>
    /// <remarks>
    ///     Nullable rather than defaulted to a scheduler of its own: a renderer that quietly starts
    ///     threads is one a test cannot make deterministic, and the engine has exactly one job
    ///     system to hand in.
    /// </remarks>
    public JobScheduler? Scheduler { get; set; }

    /// <summary>The stages, in the order they were added.</summary>
    public IReadOnlyList<RenderStage> Stages => stages;

    /// <summary>The views for the frame being built.</summary>
    public IReadOnlyList<RenderView> Views => views;

    /// <summary>The root features, in the order they were added.</summary>
    public IReadOnlyList<RootRenderFeature> Features => features;

    // --- Setup --------------------------------------------------------------

    /// <summary>Adds a stage and gives it its index.</summary>
    public RenderStage AddStage(RenderStage stage) {
        ArgumentNullException.ThrowIfNull(stage);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (stages.Count >= RenderStageMask.Capacity) {
            throw new InvalidOperationException(
                $"A render system holds at most {RenderStageMask.Capacity} stages, which is the "
                + "width of RenderStageMask. More than that is a compositor problem rather than a "
                + "bit-width one."
            );
        }

        stage.Index = stages.Count;
        stages.Add(stage);
        return stage;
    }

    /// <summary>Adds a root feature and lets it register its data.</summary>
    public TFeature AddFeature<TFeature>(TFeature feature) where TFeature : RootRenderFeature {
        ArgumentNullException.ThrowIfNull(feature);
        ObjectDisposedException.ThrowIf(disposed, this);

        feature.Index = features.Count;
        feature.System = this;
        features.Add(feature);
        feature.InitializeAll(this);
        return feature;
    }

    /// <summary>The stage with this name, or null.</summary>
    public RenderStage? FindStage(string name) =>
        stages.FirstOrDefault(stage => string.Equals(stage.Name, name, StringComparison.Ordinal));

    // --- The frame ----------------------------------------------------------

    /// <summary>Replaces the frame's views. Called once per frame, before <see cref="Draw" />.</summary>
    public void SetViews(IEnumerable<RenderView> frameViews) {
        ArgumentNullException.ThrowIfNull(frameViews);
        ObjectDisposedException.ThrowIf(disposed, this);

        views.Clear();

        foreach (var view in frameViews) {
            view.Index = views.Count;
            views.Add(view);
        }
    }

    /// <summary>Runs extract, cull, prepare and sort over the current views.</summary>
    public void Draw() {
        ObjectDisposedException.ThrowIf(disposed, this);

        Extract();
        Cull();
        Prepare();
        Sort();
    }

    /// <summary>Pulls the scene into the flat arrays.</summary>
    /// <remarks>
    ///     Serial across features on purpose. Each one may add objects, which reallocates the shared
    ///     store — so running two in parallel would mean two threads growing the same arrays. The
    ///     parallelism a feature wants is *inside* its own extraction, over its own objects, which
    ///     it can schedule itself.
    /// </remarks>
    public void Extract() {
        ObjectDisposedException.ThrowIf(disposed, this);

        foreach (var feature in features) {
            feature.ExtractAll(this);
        }
    }

    /// <summary>Tests every object against every view.</summary>
    public void Cull() {
        ObjectDisposedException.ThrowIf(disposed, this);
        Visibility.Cull(Objects, views, Scheduler);
    }

    /// <summary>Lets every feature fill data for what survived culling.</summary>
    public void Prepare() {
        ObjectDisposedException.ThrowIf(disposed, this);

        foreach (var feature in features) {
            feature.PrepareAll(this);
        }
    }

    /// <summary>Builds and orders the work list for every (view, stage) pair.</summary>
    public void Sort() {
        ObjectDisposedException.ThrowIf(disposed, this);

        foreach (var list in nodes.Values) {
            list.Clear();
        }

        foreach (var view in views) {
            foreach (var stage in stages) {
                if (!view.Stages.Contains(stage.Index)) {
                    continue;
                }

                Collect(view, stage);
            }
        }
    }

    /// <summary>The ordered work for one view and stage, empty when there is none.</summary>
    public IReadOnlyList<RenderNode> Nodes(RenderView view, RenderStage stage) {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(stage);

        return nodes.TryGetValue((view.Index, stage.Index), out var list) ? list : [];
    }

    /// <summary>
    ///     Records one view's stage into a command list, handing each feature its own nodes in runs.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         One (view, stage) at a time, because that is the unit a caller can put on a thread of
    ///         its own: each gets a <see cref="RenderDrawContext" /> with its own command list and
    ///         they share nothing that is written. The render graph decides which of those recordings
    ///         become one pass; this only produces the commands.
    ///     </para>
    ///     <para>
    ///         <strong>Features get contiguous runs, not individual nodes.</strong> The list is
    ///         already sorted by a key whose high bits are the sort group, so nodes sharing a
    ///         pipeline are adjacent — a run lets a feature bind once and draw many. Splitting at
    ///         every feature change rather than grouping by feature first is deliberate: the sort
    ///         order is what the stage promised, and reordering it here to gather a feature's work
    ///         would undo the depth ordering a transparent stage depends on.
    ///     </para>
    /// </remarks>
    public void Record(RenderView view, RenderStage stage, RenderDrawContext context) {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!nodes.TryGetValue((view.Index, stage.Index), out var list) || list.Count == 0) {
            return;
        }

        context.View = view;
        context.Stage = stage;

        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list);
        var objects = Objects.All;
        var start = 0;

        while (start < span.Length) {
            var featureIndex = objects[span[start].Object.Index].FeatureIndex;

            var end = start + 1;
            while (end < span.Length && objects[span[end].Object.Index].FeatureIndex == featureIndex) {
                end++;
            }

            if (featureIndex >= 0 && featureIndex < features.Count) {
                features[featureIndex].Draw(this, context, span[start..end]);
            }

            start = end;
        }

        context.View = null;
        context.Stage = null;
    }

    void Collect(RenderView view, RenderStage stage) {
        if (!nodes.TryGetValue((view.Index, stage.Index), out var list)) {
            // Kept across frames: the lists are the same size every frame in a steady scene, so
            // reallocating them per frame would be the renderer's largest per-frame allocation for
            // no benefit at all.
            list = [];
            nodes[(view.Index, stage.Index)] = list;
        }

        var objects = Objects.All;
        var words = Visibility.Words(view.Index);

        for (var word = 0; word < words.Length; word++) {
            var bits = words[word];

            while (bits != 0) {
                var bit = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

                var index = (word << 6) + bit;
                if (index >= objects.Length) {
                    continue;
                }

                ref readonly var candidate = ref objects[index];
                if (!candidate.Stages.Contains(stage.Index)) {
                    continue;
                }

                var id = new RenderObjectId(index);
                var feature = candidate.FeatureIndex >= 0 && candidate.FeatureIndex < features.Count
                    ? features[candidate.FeatureIndex]
                    : null;

                var group = feature?.SortGroupOf(this, id, stage) ?? candidate.SortGroup;
                list.Add(new(id, KeyFor(stage, view, candidate, group)));
            }
        }

        // Ordinal by the packed key, which orders by group then depth in one comparison — see
        // SortKey. A stable sort so that two objects with identical keys keep their id order and the
        // frame is reproducible, which is what a golden-image test depends on.
        list.Sort(static (left, right) =>
            left.Key.Value != right.Key.Value
                ? left.Key.Value.CompareTo(right.Key.Value)
                : left.Object.Index.CompareTo(right.Object.Index)
        );
    }

    static SortKey KeyFor(RenderStage stage, RenderView view, in RenderObject candidate, uint group) {
        // Measured to the sphere's surface, so a large object near the camera does not sort behind a
        // small one further away whose centre happens to be closer.
        var offset = candidate.Bounds.Center - view.Position;
        var distance = MathF.Max(offset.Length() - candidate.Bounds.Radius, 0f);

        return stage.SortMode switch {
            RenderSortMode.FrontToBack => SortKey.FrontToBack(group, distance),
            RenderSortMode.BackToFront => SortKey.BackToFront(distance),
            _ => SortKey.ByGroup(group)
        };
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        // A feature may own device resources — a material feature holds a uniform buffer per variant
        // — and nothing else is in a position to release them: the host handed the feature over and
        // this is what has held it since. Sub-features too, which are the ones that usually do.
        foreach (var feature in features) {
            foreach (var subFeature in feature.SubFeatures) {
                (subFeature as IDisposable)?.Dispose();
            }

            (feature as IDisposable)?.Dispose();
        }

        Visibility.Dispose();
        Objects.Dispose();
        nodes.Clear();
        views.Clear();
        stages.Clear();
        features.Clear();
    }
}
