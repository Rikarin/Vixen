// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Vixen.Editor.Core;

namespace Vixen.Editor.Texturing.Layers;

/// <summary>What taking a smart material out of a stack produced.</summary>
/// <param name="Material">The smart material, or <see langword="null" /> when nothing was taken.</param>
/// <param name="Status">What to tell the artist, whether or not there is a material.</param>
/// <remarks>
///     ⚠ <b>A refusal is a sentence and not an exception</b>, which is <c>MaterialBakeOutcome</c>'s
///     rule and is load bearing for the same reason: this runs from a command handler, and a throw
///     out of one takes the editor's frame with it.
/// </remarks>
sealed record SmartMaterialExtract(LayerStackAsset? Material, string Status) {
    /// <summary>Every layer, mask and mask entry this could not carry, and why, one sentence each.</summary>
    /// <remarks>
    ///     ⚠ <b>The disclosure, and the reason the drop is not silent.</b> "A smart material silently
    ///     drops the artist's strokes" and "a stack with a paint layer refuses to be saved as one"
    ///     are both defensible answers to <see cref="SmartMaterial" />'s central question; what is
    ///     not defensible is dropping without saying so, because the artist finds out by applying the
    ///     material to a second model and looking for the dirt they painted.
    /// </remarks>
    public ImmutableArray<string> Dropped { get; init; } = [];
}

/// <summary>What applying a smart material to a texture set would put on it.</summary>
/// <param name="Layers">The layers, bottom first, renamed to fit — empty when nothing applies.</param>
/// <param name="Status">What to tell the artist.</param>
/// <remarks>
///     ⚠ <b>The layers travel rather than a count, because the insertion is an undo entry and a
///     redo must put back the <em>same</em> layers.</b> Preparing and inserting in one call would
///     mean a command whose <c>Do</c> re-ran the rename — so an undo would remove <c>rust-2</c> and
///     the redo would insert <c>rust-3</c>, and every anchor an artist had written since would point
///     at a layer that no longer existed.
/// </remarks>
sealed record SmartMaterialApplied(ImmutableArray<LayerAsset> Layers, string Status) {
    /// <summary>Every layer whose id had to change to stay unique in the target, one sentence each.</summary>
    /// <remarks>
    ///     ⚠ <b>Applying the same smart material twice into one stack is the ordinary case, not the
    ///     edge.</b> A layer id is unique in a <em>set</em> — that is what an anchor and a
    ///     <c>LayerPath</c> both assume — so the second application's <c>rust-1</c> would collide
    ///     with the first's, <c>LayerStackEdit.Ambiguous</c> would report both, and the panel would
    ///     draw two rows with no controls.
    /// </remarks>
    public ImmutableArray<string> Renamed { get; init; } = [];
}

/// <summary>A <c>.vxsmartmat</c>: a layer stack without its meshes, re-bindable onto another model.</summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § M10's file.</b> Until this type the extension appeared in the plan, in
///         <c>docs/overview.md</c> and in five <c>.cs</c> <em>comments</em>, and in no type, no
///         extension constant, no reader and no verb
///         (<a href="https://github.com/Rikarin/Vixen/issues/575">#575</a>) — which is exactly the
///         shape that reads as a feature to anybody who greps for the word.
///     </para>
///     <para>
///         ⚠ <b>Half of the format table's line, and saying which half matters.</b> That line reads
///         "a stack fragment plus <em>parameter overrides</em> · a <c>.vxlayers</c> group with no
///         mesh binding". This is the second clause. The first has nowhere to live yet:
///         <c>LayerStackAsset</c> carries no parameter-override member at all, so a smart material
///         cannot yet say "apply me, but with the rust dialled down" — it applies the numbers it was
///         saved with. Filed rather than quietly implied by quoting the whole line
///         (<a href="https://github.com/Rikarin/Vixen/issues/1072">#1072</a>).
///     </para>
///     <para>
///         ⚠ <b>The file <em>is</em> a <c>.vxlayers</c>, byte for byte, and that is the design rather
///         than an economy.</b> <see cref="LayerStackYaml" /> reads and writes it, so there is no
///         second serialiser to drift, no second set of refusals for an unknown blend mode, and the
///         round trip the explode differential already asserts covers this format too. What makes a
///         smart material a smart material is not its syntax but three invariants
///         <see cref="Extract" /> establishes and <see cref="Apply" /> re-establishes: no model, no
///         mesh, and nothing painted.
///     </para>
///     <para>
///         ⚠ <b>What a smart material drops relative to a stack is decided by whether the thing
///         survives a change of model, and the two hard cases fall on opposite sides.</b> A mask
///         driven by a <c>curvature</c> bake is portable — <c>Source/Mesh Map</c> names no image at
///         all, so the same mask reads the new mesh's own bakes with no rewiring, which is § D10's
///         claim and the whole reason a generator is worth authoring. A mask driven by a
///         hand-painted canvas is not: a <c>.vxpaint</c> is texels in <em>this</em> model's atlas,
///         and carrying it onto another model puts the artist's strokes at UV positions that mean
///         something else entirely. So the bakes, the generators, the anchors, the imported textures
///         and the graph fills all travel, and the paint does not.
///     </para>
///     <para>
///         ⚠ <b>And the paint is dropped rather than refused — loudly, one sentence per thing</b>
///         (<see cref="SmartMaterialExtract.Dropped" />). Refusing the whole save is the other
///         defensible answer and it was rejected because M9 makes a paint layer ordinary rather than
///         exotic: a rule that a stack containing one can never become a smart material would make
///         the feature unreachable for most real stacks, and an artist's answer to it would be to
///         delete the paint layer by hand, which is the same drop with no record of what went.
///     </para>
///     <para>
///         ⚠ <b>A dropped paint <em>mask</em> becomes a constant zero and never
///         <see cref="LayerMaskSource.None" />, which is the one place this file could have been
///         silently wrong.</b> <c>None</c> does not mean "no coverage", it means <em>no mask</em> —
///         the layer then writes everywhere. So the obvious spelling of "remove the mask I cannot
///         carry" turns a layer that painted a rust patch into one that covers the whole model, and
///         dropping coverage would have <em>increased</em> it. A constant zero folds into the
///         layer's opacity (<a href="https://github.com/Rikarin/Vixen/issues/789">#789</a>) and the
///         layer contributes nothing until the artist paints its mask again on the new model, which
///         is what "this was painted and could not come" should look like.
///     </para>
/// </remarks>
static class SmartMaterial {
    /// <summary>What a smart material is written as.</summary>
    public const string Extension = ".vxsmartmat";

    /// <summary>Where a project keeps them, under <c>Assets/</c> — doc 48 § M10's shelf folder.</summary>
    /// <remarks>
    ///     ⚠ <b>A named folder rather than a walk of the project, which is
    ///     <see cref="TextureNodeLibrary.CompoundFolder" />'s decision and is taken here for its
    ///     reason.</b> The shelf is the folder a third party drops content into, and a convention
    ///     somebody can read beats a heuristic that has to guess: a walk would offer every stack
    ///     fragment anybody ever saved anywhere in the project, and the folder not existing is the
    ///     ordinary case and offers nothing rather than failing.
    /// </remarks>
    public const string ShelfFolder = "SmartMaterials";

    /// <summary>Which folder a project's smart materials are read from and written to.</summary>
    /// <param name="assets">A project's <c>Assets/</c> folder, or <see langword="null" /> for none.</param>
    /// <returns>The folder, or <see langword="null" /> when there is no project.</returns>
    /// <remarks>
    ///     Here rather than at each caller, for <see cref="TextureNodeLibrary.FolderOf" />'s reason:
    ///     a second spelling of the convention is a second answer the day it moves.
    /// </remarks>
    public static string? FolderOf(string? assets) =>
        assets is { Length: > 0 } ? Path.Combine(assets, ShelfFolder) : null;

    /// <summary>What the shelf holds, by name, ordered.</summary>
    /// <param name="assets">A project's <c>Assets/</c> folder, or <see langword="null" /> for none.</param>
    /// <returns>The file names without their extension. Empty when the folder does not exist.</returns>
    /// <remarks>
    ///     ⚠ <b>Names rather than paths, because the only caller is a sentence.</b> A verb that
    ///     cannot find a smart material to apply has to say what there <em>is</em>, and an artist
    ///     reading "the shelf holds Painted Metal, Rusted Iron" can act on it where "no smart
    ///     material is selected" leaves them guessing whether the project has any at all.
    /// </remarks>
    public static IReadOnlyList<string> Shelf(string? assets) {
        if (FolderOf(assets) is not { } folder || !Directory.Exists(folder)) {
            return [];
        }

        return Directory.EnumerateFiles(folder, "*" + Extension, SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Takes one texture set's layers out of a stack as a smart material.</summary>
    /// <param name="stack">The stack.</param>
    /// <param name="name">What the smart material should be called.</param>
    /// <param name="setName">Which set, or empty for the first.</param>
    /// <returns>The material and what to say about it. Never null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stack" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name" /> is empty.</exception>
    /// <remarks>
    ///     ⚠ <b>One set and not the stack, because a smart material is applied <em>to</em> a set.</b>
    ///     A stack's sets are the material slots of one model
    ///     (<a href="https://github.com/Rikarin/Vixen/issues/920">#920</a>), so "every set" is a fact
    ///     about the model this stack was authored on and is the first thing that stops being true on
    ///     the next one. The channels come along because a layer names the channels it writes by
    ///     usage, and a reader that had the layers without them could not say which of the target's
    ///     maps this material was authored against.
    /// </remarks>
    public static SmartMaterialExtract Extract(LayerStackAsset stack, string name, string setName = "") {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (stack.Sets.Count == 0) {
            return new(
                null,
                "Nothing saved: this stack has no texture set, so there are no layers to take."
            );
        }

        var set = LayerStackEdit.SetFor(stack, setName) ?? stack.Sets[0];

        if (set.Layers.Count == 0) {
            return new(
                null,
                $"Nothing saved: texture set '{set.Name}' has no layers, and a smart material with none "
                + "would apply nothing wherever it was dropped."
            );
        }

        List<string> dropped = [];
        var layers = Portable(set.Layers, dropped);

        if (layers.Count == 0) {
            return new(
                null,
                $"Nothing saved: every layer of texture set '{set.Name}' is painted, and painted pixels "
                + "are the one thing a smart material cannot carry onto another model. "
                + string.Join(" ", dropped)
            ) { Dropped = [.. dropped] };
        }

        // ⚠ `Model` and the set's `Mesh` are left at their defaults rather than copied, and that
        // omission is the whole of what makes the file a smart material: those two are the mesh
        // binding, and a stack fragment carrying one would re-bind the artist to the model it came
        // off the moment they applied it.
        LayerStackAsset material = new() {
            Version = LayerStackAsset.CurrentVersion,
            Name = name,
            BaseWidth = stack.BaseWidth,
            BaseHeight = stack.BaseHeight,
            Seed = stack.Seed,
            Sets = [
                new() {
                    Name = set.Name,
                    Channels = [..set.Channels.Select(channel => channel with { Default = [..channel.Default] })],
                    Layers = layers
                }
            ]
        };

        return new(
            material,
            $"'{name}': {Count(layers.Count)} from texture set '{set.Name}', with no model binding."
            + (dropped.Count > 0 ? " ⚠ " + string.Join(" ", dropped) : "")
        ) { Dropped = [.. dropped] };
    }

    /// <summary>Works out what a smart material would put on a texture set, without putting it there.</summary>
    /// <param name="target">The set the layers would go into. <b>Read, never mutated.</b></param>
    /// <param name="material">The smart material, as read off its file.</param>
    /// <returns>The layers to insert and what to say about it. Never null.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>On top rather than replacing, which is what an artist means by applying one.</b>
    ///         <c>TextureSetAsset.Layers</c> is bottom first, so "on top" is the end of the list; a
    ///         smart material that cleared the set would make the second one an artist tried destroy
    ///         the first, with the undo history as the only way back.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The target's channels are kept and the material's are not merged in.</b> A layer
    ///         names the channels it writes by usage and <c>LayerStackGraph</c> walks the
    ///         <em>set's</em> channels, so a layer naming a usage the target does not produce simply
    ///         writes nothing — no refusal is needed and none is given. What is given is a sentence
    ///         naming those usages, because "I applied Rusted Iron and the height did not change" has
    ///         exactly one cause and it is this.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It sanitises again rather than trusting the file.</b> A <c>.vxlayers</c> renamed
    ///         to <c>.vxsmartmat</c> by hand is a thing people do, and it carries paint references
    ///         into a stack whose <c>.vxpaint</c> files are somewhere else entirely —
    ///         <c>LayerPaint.NameFor</c> keys them on the stack, the set and the layer.
    ///     </para>
    /// </remarks>
    public static SmartMaterialApplied Prepare(TextureSetAsset target, LayerStackAsset material) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(material);

        if (material.Sets.Count != 1) {
            return new(
                [],
                "Nothing applied: a smart material holds exactly one texture set's layers and this "
                + $"file holds {material.Sets.Count.ToString(CultureInfo.InvariantCulture)}."
            );
        }

        List<string> dropped = [];
        var layers = Portable(material.Sets[0].Layers, dropped);

        if (layers.Count == 0) {
            return new(
                [],
                "Nothing applied: this smart material carries no layer that survives a change of "
                + "model. " + string.Join(" ", dropped)
            );
        }

        List<string> renamed = [];

        Rename(target, layers, renamed);

        var wanted = material.Sets[0].Channels.Select(channel => channel.Usage);
        var has = target.Channels.Select(channel => channel.Usage).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = wanted.Where(usage => !has.Contains(usage)).ToArray();

        return new(
            [.. layers],
            $"Applied '{material.Name}': {Count(layers.Count)} on top of texture set '{target.Name}'."
            + (missing.Length > 0
                ? $" ⚠ It was authored against {string.Join(", ", missing.Select(one => "'" + one + "'"))}, "
                + "which this set does not produce, so nothing it writes there reaches a map."
                : "")
            + (dropped.Count > 0 ? " ⚠ " + string.Join(" ", dropped) : "")
            + (renamed.Count > 0 ? " " + string.Join(" ", renamed) : "")
        ) { Renamed = [.. renamed] };
    }

    /// <summary>A file name a person's material name cannot escape <c>Assets/</c> through.</summary>
    /// <param name="name">What the artist called it.</param>
    /// <returns>The stem, with every separator and every invalid character replaced.</returns>
    /// <remarks>
    ///     ⚠ <b><c>ProjectMaterialBaker.Safe</c>'s reason, which is a defect that shipped once</b> —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/680">#680</a>. "Ship / Hull" is a
    ///     perfectly good name for a material and a path traversal in a file system, and the sanitise
    ///     belongs where the path is built rather than at whichever caller happens to remember.
    /// </remarks>
    public static string Safe(string name) {
        ArgumentNullException.ThrowIfNull(name);

        var invalid = Path.GetInvalidFileNameChars();
        var built = new char[name.Length];

        for (var at = 0; at < name.Length; at++) {
            var character = name[at];

            built[at] = character is '.' or ' ' || Array.IndexOf(invalid, character) >= 0 ? '_' : character;
        }

        var stem = new string(built).Trim('_');

        return stem.Length > 0 ? stem : "SmartMaterial";
    }

    /// <summary>Deep-copies a list of layers, leaving behind everything a change of model invalidates.</summary>
    /// <param name="layers">The layers, bottom first.</param>
    /// <param name="dropped">Where a sentence per dropped thing goes.</param>
    /// <returns>The copies.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Deep, because a record's <c>with</c> is not.</b> <c>LayerAsset.Children</c>,
    ///         <c>Values</c>, <c>Textures</c>, <c>Settings</c> and <c>Mask</c> are mutable references,
    ///         so a shallow copy would hand the smart material the <em>live</em> stack's lists — and
    ///         the first edit an artist made to either would show up in the other.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two passes, because an anchor can name a layer this pass is about to drop.</b>
    ///         A mask anchored to a paint layer is a mask reading pixels that are not coming, and
    ///         leaving the reference in place would be a <c>.vxsmartmat</c> that refuses to compile
    ///         on the first model anybody applies it to. The ids that go are collected first and the
    ///         anchors naming them are treated exactly as a painted mask is — for the same reason and
    ///         with the same constant zero.
    ///     </para>
    /// </remarks>
    static List<LayerAsset> Portable(List<LayerAsset> layers, List<string> dropped) {
        HashSet<string> gone = new(StringComparer.Ordinal);

        Collect(layers);

        return Copy(layers);

        void Collect(List<LayerAsset> list) {
            foreach (var layer in list) {
                if (layer.Kind == LayerKind.Paint) {
                    gone.Add(layer.Id);

                    continue;
                }

                Collect(layer.Children);
            }
        }

        List<LayerAsset> Copy(List<LayerAsset> list) {
            List<LayerAsset> copies = [];

            foreach (var layer in list) {
                if (layer.Kind == LayerKind.Paint) {
                    dropped.Add(
                        $"Layer '{Named(layer)}' is painted, so its pixels stayed with the model they "
                        + "were painted on and the layer did not come."
                    );

                    continue;
                }

                copies.Add(
                    layer with {
                        Channels = [.. layer.Channels],
                        Values = layer.Values.ToDictionary(
                            entry => entry.Key,
                            entry => entry.Value.ToArray(),
                            StringComparer.Ordinal
                        ),
                        Textures = new Dictionary<string, string>(layer.Textures, StringComparer.Ordinal),
                        Settings = layer.Settings.ToDictionary(
                            entry => entry.Key,
                            entry => entry.Value.ToArray(),
                            StringComparer.Ordinal
                        ),

                        // ⚠ #1079's member, and it has to be here for the reason the remark above
                        // gives: a filter layer's settings are a mutable dictionary, so a `with`
                        // that left it out would hand the smart material the live stack's own — and
                        // an artist changing a compound's mode in one would change it in the other.
                        // The list of members this method deep-copies is the one thing here a new
                        // member on `LayerAsset` silently falls out of, which is why
                        // `SmartMaterialTests` walks the record by reflection rather than by name.
                        Texts = new Dictionary<string, string>(layer.Texts, StringComparer.Ordinal),

                        // ⚠ A paint layer's own `Paint` is gone with the layer, but a *filter* or a
                        // *group* can carry a stale one off a hand-written file — and a reference to
                        // a `.vxpaint` that names another stack is the thing this whole pass is for.
                        Paint = "",
                        Mask = Mask(layer),
                        Children = Copy(layer.Children)
                    }
                );
            }

            return copies;
        }

        MaskAsset Mask(LayerAsset layer) {
            var mask = layer.Mask;
            var carried = mask with {
                Layers = [],
                Effects = [
                    ..mask.Effects.Select(
                        effect => effect with {
                            Values = effect.Values.ToDictionary(
                                entry => entry.Key,
                                entry => entry.Value.ToArray(),
                                StringComparer.Ordinal
                            ),
                            Texts = new Dictionary<string, string>(effect.Texts, StringComparer.Ordinal)
                        }
                    )
                ]
            };

            if (Stranded(mask.Source, mask.Anchor)) {
                dropped.Add(
                    $"The mask of layer '{Named(layer)}' {Because(mask.Source)}, so it came back as a "
                    + "constant zero and that layer writes nothing until it is masked again."
                );

                // ⚠ Zero and not `None`. `None` means *no mask* and the layer would then write
                // everywhere, so the obvious spelling of "drop the coverage I cannot carry" is the
                // one that multiplies it.
                carried = carried with {
                    Source = LayerMaskSource.Constant,
                    Value = 0f,
                    Paint = "",
                    Anchor = ""
                };
            }

            foreach (var entry in mask.Layers) {
                if (!Stranded(entry.Source, entry.Anchor)) {
                    carried.Layers.Add(entry);

                    continue;
                }

                dropped.Add(
                    $"One entry of the mask of layer '{Named(layer)}' {Because(entry.Source)}, so it is "
                    + "switched off rather than carried."
                );

                // ⚠ Disabled rather than zeroed, which the base cannot be and this cannot not be. An
                // entry composites over what is beneath it with its own operator, so a zero is
                // "cover nothing" under `Multiply` and "cover nothing, and throw away everything
                // under me" under `Copy`; a disabled entry is skipped and is neutral under all
                // sixteen.
                carried.Layers.Add(entry with { Enabled = false, Paint = "", Anchor = "" });
            }

            return carried;
        }

        bool Stranded(LayerMaskSource source, string anchor) =>
            source == LayerMaskSource.Paint
            || (source == LayerMaskSource.Anchor && gone.Contains(anchor));
    }

    /// <summary>Why a mask source could not come.</summary>
    /// <param name="source">It.</param>
    /// <returns>The clause, to be read after "the mask of layer 'x'".</returns>
    static string Because(LayerMaskSource source) =>
        source == LayerMaskSource.Paint
            ? "is painted, and painted pixels are in this model's atlas"
            : "is anchored to a painted layer that did not come";

    /// <summary>Gives every layer an id the target set does not already answer to, anchors and all.</summary>
    /// <param name="target">The set they are going into.</param>
    /// <param name="layers">The layers. Mutated in place.</param>
    /// <param name="renamed">Where a sentence per rename goes.</param>
    /// <remarks>
    ///     ⚠ <b>Only on a collision, and the ids that do not collide keep their spelling.</b>
    ///     <c>LayerStackEdit.FreeId</c>'s own remark is that a readable counted id is what makes
    ///     <c>anchor: rust-1</c> a line somebody can resolve a merge conflict in; re-minting every id
    ///     on every apply would trade that away to solve a problem only the second apply has.
    ///     ⚠ And the anchors are rewritten through the same map, because an anchor is a reference
    ///     <em>into the smart material</em> — one left pointing at the old id would either dangle or,
    ///     worse, silently resolve to the target's own layer of that name.
    /// </remarks>
    static void Rename(TextureSetAsset target, List<LayerAsset> layers, List<string> renamed) {
        HashSet<string> taken = new(StringComparer.Ordinal);

        Walk(target.Layers, id => taken.Add(id));

        Dictionary<string, string> map = new(StringComparer.Ordinal);

        Walk(
            layers,
            id => {
                if (taken.Add(id)) {
                    return true;
                }

                var stem = id.Length > 0 ? id : "layer";

                for (var number = 2; ; number++) {
                    var wanted = stem + "-" + number.ToString(CultureInfo.InvariantCulture);

                    if (taken.Add(wanted)) {
                        map[id] = wanted;
                        renamed.Add($"'{id}' was already taken in '{target.Name}', so it came in as '{wanted}'.");

                        return true;
                    }
                }
            }
        );

        if (map.Count > 0) {
            Rewrite(layers);
        }

        void Rewrite(List<LayerAsset> list) {
            for (var at = 0; at < list.Count; at++) {
                var layer = list[at];
                var mask = layer.Mask;

                if (mask.Source == LayerMaskSource.Anchor && map.TryGetValue(mask.Anchor, out var moved)) {
                    mask = mask with { Anchor = moved };
                }

                for (var entry = 0; entry < mask.Layers.Count; entry++) {
                    if (mask.Layers[entry] is { Source: LayerMaskSource.Anchor } anchored
                        && map.TryGetValue(anchored.Anchor, out var also)) {
                        mask.Layers[entry] = anchored with { Anchor = also };
                    }
                }

                list[at] = layer with {
                    Id = map.TryGetValue(layer.Id, out var renamedTo) ? renamedTo : layer.Id,
                    Mask = mask
                };

                Rewrite(list[at].Children);
            }
        }

        static void Walk(List<LayerAsset> list, Func<string, bool> visit) {
            foreach (var layer in list) {
                visit(layer.Id);
                Walk(layer.Children, visit);
            }
        }
    }

    /// <summary>The layer's name, or its id, or a word — whichever a sentence can use.</summary>
    /// <param name="layer">It.</param>
    /// <returns>The name.</returns>
    static string Named(LayerAsset layer) =>
        layer.Name.Length > 0 ? layer.Name : layer.Id.Length > 0 ? layer.Id : "(unnamed)";

    /// <summary>A count of layers, with the noun agreeing.</summary>
    /// <param name="count">How many.</param>
    /// <returns>The phrase.</returns>
    static string Count(int count) =>
        count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " layer" : " layers");
}

/// <summary>A smart material dropped onto a texture set, as <b>one</b> undo entry.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>One entry and not one per layer, which is why this exists rather than a loop over
///         <see cref="AddLayerCommand" />.</b> A smart material is six or ten layers, and an artist
///         who applied one and wanted it gone would otherwise press undo ten times and be unable to
///         tell, from the entry names, where the material started.
///     </para>
///     <para>
///         ⚠ <b>The layers are settled before the command is built</b> — <see cref="SmartMaterial.Prepare" />
///         — so <c>Do</c> is deterministic and a redo inserts exactly what the undo took out. A
///         command that re-prepared would re-mint the ids on every redo.
///     </para>
///     <para>
///         ⚠ <b>The undo removes by id rather than by value.</b> <see cref="LayerAsset" /> is a
///         record whose <c>Children</c>, <c>Values</c> and <c>Settings</c> are mutable references, so
///         its generated equality compares those by <em>reference</em> — and every edit an artist
///         makes to an applied layer goes through <c>SetLayerCommand</c>, which replaces the record.
///         A <c>List.Remove</c> would then silently find nothing and the undo would leave the whole
///         material in place.
///     </para>
/// </remarks>
sealed class ApplySmartMaterialCommand : IEditorCommand {
    readonly LayerStackDocument document;
    readonly string set;
    readonly ImmutableArray<LayerAsset> layers;

    /// <summary>Records an application.</summary>
    /// <param name="document">The stack it happens in.</param>
    /// <param name="set">The <see cref="TextureSetAsset.Name" /> the layers go on.</param>
    /// <param name="layers">What goes on, bottom first, ids already settled.</param>
    /// <param name="name">What the undo entry says.</param>
    /// <exception cref="ArgumentNullException">The document or the set is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name" /> is empty, or there are no layers.</exception>
    public ApplySmartMaterialCommand(
        LayerStackDocument document,
        string set,
        ImmutableArray<LayerAsset> layers,
        string name
    ) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (layers.IsDefaultOrEmpty) {
            throw new ArgumentException("An application that puts no layer on is not an undo entry.", nameof(layers));
        }

        this.document = document;
        this.set = set;
        this.layers = layers;

        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Appended rather than inserted at a remembered index.</b> "On top" is what an artist
    ///     asked for, and the top of the set is wherever the top is when the redo runs — a recorded
    ///     index would put the material back under whatever had been added since.
    /// </remarks>
    public void Do(EditorContext context) {
        if (LayerStackEdit.SetFor(document.Document, set) is not { } target) {
            return;
        }

        target.Layers.AddRange(layers);
    }

    /// <inheritdoc />
    public void Undo(EditorContext context) {
        foreach (var layer in layers) {
            LayerStackEdit.Remove(document.Document, new(set, layer.Id), out _);
        }
    }

    /// <inheritdoc />
    /// <inheritdoc cref="AddLayerCommand.TryMergeWith" path="/remarks" />
    public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;

        return false;
    }
}
