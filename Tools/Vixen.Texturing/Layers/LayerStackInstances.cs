// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Texturing.Layers;

/// <summary>
///     What an <see cref="LayerKind.Instance" /> layer is when a stack compiles: its source, in its place, with what stays
///     its own (Patina #108).
/// </summary>
/// <remarks>
///     <para>
///         <b>Resolved at compile and never written back.</b> The file holds the reference, so an edit to the source is an
///         edit to every instance the next time anything compiles, with nothing to keep in step — the property an
///         instance exists for.
///     </para>
///     <para>
///         <b>An anchor point a copy reads is the source's.</b> In the source's own set the copy reads it where the source
///         does, so the instance renders as its source does, above the point as any reader must be. A source in another set
///         that reads one is refused with a warning and shows nothing, since that set's anchor points are not this one's.
///     </para>
///     <para>
///         ⚠ <b>A copy's own layers are renamed under the instance's id</b> — <c>instance/child</c> — and publish no
///         anchor point. Two layers of one id are refused by the compiler, and the source's anchor points are the
///         source's: a folder instanced twice would otherwise publish each of its points three times.
///     </para>
/// </remarks>
public static class LayerStackInstances {
    /// <summary>How deep instances of instances go before the walk calls it a cycle. A hang check, not a design limit.</summary>
    public const int MaxDepth = 32;

    /// <summary>The set with every instance replaced by what it stands for.</summary>
    /// <param name="stack">The document, whose other sets an instance may read.</param>
    /// <param name="set">The set being compiled.</param>
    /// <param name="problems">
    ///     A refusal for each instance in a cycle, and a warning for each whose source has gone. Either contributes nothing.
    /// </param>
    /// <returns>The set itself when it holds no instance.</returns>
    public static TextureSetAsset Resolve(
        LayerStackAsset stack,
        TextureSetAsset set,
        out IReadOnlyList<LayerStackProblem> problems
    ) {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(set);

        List<LayerStackProblem> found = [];

        problems = found;

        return Holds(set.Layers) ? set with { Layers = Layers(stack, set.Name, set.Layers, [], found) } : set;
    }

    /// <summary>
    ///     Whether a layer standing for <paramref name="source" /> would stand, through instances, for itself — the edit a
    ///     host refuses before it is made.
    /// </summary>
    /// <param name="stack">The document, with every set an instance may read.</param>
    /// <param name="set">The set of the layer that would become the instance.</param>
    /// <param name="layer">Its id.</param>
    /// <param name="source">What it would stand for.</param>
    /// <returns><see langword="true" /> when that is a cycle.</returns>
    public static bool Cycles(LayerStackAsset stack, string set, string layer, InstanceReference source) {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(source);

        HashSet<(string, string)> seen = [(set, layer)];

        return Reaches(stack, Key(set, source), seen, 0);
    }

    /// <summary>The instances of a layer, in every set of the stack, by set name and id.</summary>
    /// <param name="stack">The document.</param>
    /// <param name="set">The source's set.</param>
    /// <param name="layer">The source's id.</param>
    /// <returns>Each instance that names it.</returns>
    public static IReadOnlyList<(string Set, string Layer)> Of(LayerStackAsset stack, string set, string layer) {
        ArgumentNullException.ThrowIfNull(stack);

        List<(string, string)> found = [];

        foreach (var held in stack.Sets) {
            foreach (var instance in Walk(held.Layers)) {
                if (Key(held.Name, instance.Instance) == (set, layer)) {
                    found.Add((held.Name, instance.Id));
                }
            }
        }

        return found;
    }

    static bool Reaches(LayerStackAsset stack, (string Set, string Layer) at, HashSet<(string, string)> seen, int depth) {
        if (!seen.Add(at) || depth > MaxDepth) {
            return true;
        }

        if (stack.SetNamed(at.Set) is not { } set || LayerStackEdit.Find(stack, new(set.Name, at.Layer)) is not { } layer) {
            return false;
        }

        foreach (var instance in Walk([layer])) {
            if (Reaches(stack, Key(at.Set, instance.Instance), [.. seen], depth + 1)) {
                return true;
            }
        }

        return false;
    }

    static (string Set, string Layer) Key(string set, InstanceReference reference) =>
        (reference.Set.Length > 0 ? reference.Set : set, reference.Layer);

    static IEnumerable<LayerAsset> Walk(List<LayerAsset> layers) {
        foreach (var layer in layers) {
            if (layer.Kind == LayerKind.Instance) {
                yield return layer;
            }

            foreach (var inside in Walk(layer.Children)) {
                yield return inside;
            }
        }
    }

    static bool Holds(List<LayerAsset> layers) => Walk(layers).Any();

    static List<LayerAsset> Layers(
        LayerStackAsset stack,
        string set,
        List<LayerAsset> layers,
        List<(string, string)> path,
        List<LayerStackProblem> problems
    ) => [.. layers.Select(layer => Layer(stack, set, layer, path, problems))];

    static LayerAsset Layer(
        LayerStackAsset stack,
        string set,
        LayerAsset layer,
        List<(string, string)> path,
        List<LayerStackProblem> problems
    ) {
        if (layer.Kind != LayerKind.Instance) {
            return Holds(layer.Children)
                ? layer with { Children = Layers(stack, set, layer.Children, path, problems) }
                : layer;
        }

        var key = Key(set, layer.Instance);
        var named = layer.Name.Length > 0 ? layer.Name : layer.Id;

        if (path.Contains(key) || path.Count >= MaxDepth) {
            problems.Add(
                LayerStackProblem.Refusal(
                    layer.Id,
                    $"Instance '{named}' stands, through instances, for itself. A layer cannot be its own source; point "
                    + "it at another layer."
                )
            );

            return Nothing(layer);
        }

        if (stack.SetNamed(key.Set) is null || LayerStackEdit.Find(stack, new(key.Set, key.Layer)) is not { } source) {
            problems.Add(
                LayerStackProblem.Warning(
                    layer.Id,
                    $"Instance '{named}' stands for layer '{key.Layer}' of texture set '{key.Set}', which is not there, "
                    + "so it shows nothing."
                )
            );

            return Nothing(layer);
        }

        // The source as it compiles, instances inside it resolved in its own set.
        var resolved = Layer(stack, key.Set, source, [.. path, key], problems);

        // ⚠ An anchor point is a picture of its own set's stack, so another set's source that reads one would read
        // whatever this set publishes under that name, or nothing. Refused here, by name, rather than either.
        if (!string.Equals(key.Set, set, StringComparison.Ordinal)
            && LayerStackEdit.AnchorUses(new() { Layers = [resolved] }) is [var use, ..]) {
            problems.Add(
                LayerStackProblem.Warning(
                    layer.Id,
                    $"Instance '{named}' stands for layer '{key.Layer}' of texture set '{key.Set}', whose {use.Where} "
                    + $"reads the anchor point '{use.Reference.Anchor}'. An anchor point is read only in its own texture "
                    + "set, so the instance shows nothing; read a generator, a picture or a mesh map there instead."
                )
            );

            return Nothing(layer);
        }

        var owned = Owned(resolved);

        return Prefixed(owned, layer.Id) with {
            Id = layer.Id,
            Name = layer.Name,
            Enabled = layer.Enabled,
            Opacity = layer.Opacity,
            Blend = layer.Blend,
            AnchorPoint = layer.AnchorPoint,
            Mask = Narrowed(owned.Mask, layer.Mask),
            Instance = new()
        };
    }

    /// <summary>A layer that composites nothing and still publishes the instance's anchor point.</summary>
    static LayerAsset Nothing(LayerAsset layer) =>
        layer with { Kind = LayerKind.Group, Children = [], Enabled = false, Instance = new() };

    /// <summary>The source's mask, narrowed by the instance's own entries: they multiply over it, or stand alone.</summary>
    static MaskAsset Narrowed(MaskAsset source, MaskAsset own) {
        if (own.Layers.Count == 0) {
            return source;
        }

        var something = source.Source != LayerMaskSource.None || source.Layers.Count > 0;

        return source with {
            Layers = [
                ..source.Layers,
                ..own.Layers.Select((entry, index) =>
                    something || index > 0 ? entry with { Blend = LayerBlendMode.Multiply } : entry
                )
            ]
        };
    }

    static LayerAsset Prefixed(LayerAsset layer, string prefix) =>
        layer with {
            Children = [
                ..layer.Children.Select(child => Prefixed(Owned(child), prefix) with {
                        Id = $"{prefix}/{child.Id}", AnchorPoint = ""
                    }
                )
            ]
        };

    /// <summary>A copy of a layer that shares no mutable member with it, its children excepted.</summary>
    /// <remarks>
    ///     ⚠ <b>A <c>with</c> copies references</b>, and a filter layer's <see cref="LayerAsset.Inputs" />, its numbers and
    ///     its mask's effects are dictionaries and lists a host edits in place. An instance that shared them with its source
    ///     would be changed by an edit meant for the source alone, after the copy was taken, and a host that compares a
    ///     resolved set with the one before would see no change. <see cref="Prefixed" /> copies the children.
    /// </remarks>
    static LayerAsset Owned(LayerAsset layer) =>
        layer with {
            Channels = [.. layer.Channels],
            Values = Numbers(layer.Values),
            Textures = new(layer.Textures, StringComparer.Ordinal),
            Settings = Numbers(layer.Settings),
            Texts = new(layer.Texts, StringComparer.Ordinal),
            Inputs = Sources(layer.Inputs),
            Mask = layer.Mask with {
                GeneratorValues = Numbers(layer.Mask.GeneratorValues),
                GeneratorTexts = new(layer.Mask.GeneratorTexts, StringComparer.Ordinal),
                GeneratorInputs = new(layer.Mask.GeneratorInputs, StringComparer.Ordinal),
                Layers = [
                    ..layer.Mask.Layers.Select(static entry => entry with {
                            GeneratorValues = Numbers(entry.GeneratorValues),
                            GeneratorTexts = new(entry.GeneratorTexts, StringComparer.Ordinal),
                            GeneratorInputs = new(entry.GeneratorInputs, StringComparer.Ordinal)
                        }
                    )
                ],
                Effects = [
                    ..layer.Mask.Effects.Select(static effect => effect with {
                            Values = Numbers(effect.Values),
                            Texts = new(effect.Texts, StringComparer.Ordinal),
                            Inputs = Sources(effect.Inputs)
                        }
                    )
                ]
            }
        };

    static Dictionary<string, float[]> Numbers(Dictionary<string, float[]> numbers) =>
        numbers.ToDictionary(static entry => entry.Key, static entry => entry.Value.ToArray(), StringComparer.Ordinal);

    static Dictionary<string, LayerInputAsset> Sources(Dictionary<string, LayerInputAsset> inputs) =>
        inputs.ToDictionary(
            static entry => entry.Key,
            static entry => entry.Value with {
                Values = Numbers(entry.Value.Values),
                Texts = new(entry.Value.Texts, StringComparer.Ordinal)
            },
            StringComparer.Ordinal
        );
}
