// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Yaml;

namespace Vixen.Texturing.Layers;

/// <summary>A <c>.vxlayers</c>, read and written key by key.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Hand-mapped rather than <c>[DataContract]</c>, and the reason is a landmine this
///         slice stepped on: <b>a plugin's own entry assembly cannot declare a data contract.</b></b>
///         <c>PluginLoadContext</c> sends every <c>Vixen.*</c> dependency to the default context and
///         loads the entry assembly itself into the collectible one — that is what makes
///         <c>PluginHost.WaitForCollection</c> mean anything — so the plugin's assembly is loaded
///         <em>twice</em>, its descriptor module initializer runs twice, and the process-wide
///         <c>TypeRegistry</c> refuses the second: <i>"Both 'X' and 'X' claim the name 'X'"</i>, out
///         of a <c>TypeInitializationException</c> that takes the whole plugin load with it.
///         <c>TexturingCollectionTests</c> is what caught it, in this assembly, immediately.
///         <a href="https://github.com/Rikarin/Vixen/issues/798">#798</a> — and
///         <c>Vixen.Editor.Terrain</c> has the same landmine unarmed, because nothing loads it from
///         a folder.
///     </para>
///     <para>
///         <b>What the hand mapping buys back, having been forced:</b> a member equal to its default
///         is not written. The generated path wrote <c>graph: ''</c>, <c>filter: Levels</c> and a
///         five-key <c>mask:</c> under every layer whether or not it had one — 202 lines for a
///         seven-layer stack. A <c>.vxlayers</c> is a file people merge, so the keys that are there
///         should be the ones somebody chose.
///     </para>
///     <para>
///         ⚠ <b>Every key an unknown value could arrive under is a refusal with the path in it.</b>
///         A stack written by a later build and read by this one is a real case — the version is the
///         first key in the file — and silently taking the default for a blend mode nobody here
///         knows is the failure mode <c>Colour/Blend</c>'s own remarks name: a picture, not an error.
///     </para>
/// </remarks>
public static class LayerStackYaml {
    /// <summary>The stack as YAML.</summary>
    /// <param name="stack">The stack.</param>
    /// <returns>The text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stack" /> is null.</exception>
    public static string Write(LayerStackAsset stack) {
        ArgumentNullException.ThrowIfNull(stack);

        YamlMapping root = new();

        root.Set("version", Whole(stack.Version));
        root.Set("name", Text(stack.Name));

        // ⚠ Only when it is bound, like every other member equal to its default. A `model: ''` under
        // every stack that has never been near a mesh is a key nobody chose, and this file's own
        // argument is that the keys in a `.vxlayers` should be the ones somebody did.
        if (stack.Model.Length > 0) {
            root.Set("model", Text(stack.Model));
        }

        root.Set("baseWidth", Whole(stack.BaseWidth));
        root.Set("baseHeight", Whole(stack.BaseHeight));

        if (stack.Seed != 0) {
            root.Set("seed", new YamlScalar(stack.Seed.ToString(CultureInfo.InvariantCulture), YamlScalarStyle.Plain));
        }

        YamlSequence sets = new();

        foreach (var set in stack.Sets) {
            sets.Add(Write(set));
        }

        root.Set("sets", sets);

        return YamlWriter.Write(root);
    }

    /// <summary>A stack read back.</summary>
    /// <param name="text">What <see cref="Write(LayerStackAsset)" /> wrote.</param>
    /// <returns>The stack.</returns>
    /// <exception cref="YamlBindingException">A key holds something this build cannot read.</exception>
    /// <exception cref="YamlParseException">It is not YAML.</exception>
    public static LayerStackAsset Read(string text) {
        var root = Mapping(YamlReader.Read(text), "");

        List<TextureSetAsset> sets = [];

        if (root.TryGet("sets", out var node)) {
            var index = 0;

            foreach (var item in Sequence(node, "sets")) {
                sets.Add(ReadSet(item, $"sets[{index++}]"));
            }
        }

        return new() {
            Version = Integer(root, "version", LayerStackAsset.CurrentVersion, ""),
            Name = String(root, "name", ""),
            Model = String(root, "model", ""),
            BaseWidth = Integer(root, "baseWidth", 1024, ""),
            BaseHeight = Integer(root, "baseHeight", 1024, ""),
            Seed = (uint)Integer(root, "seed", 0, "", unsigned: true),
            Sets = sets
        };
    }

    static YamlMapping Write(TextureSetAsset set) {
        YamlMapping mapping = new();

        mapping.Set("name", Text(set.Name));

        if (set.Mesh.Length > 0) {
            mapping.Set("mesh", Text(set.Mesh));
        }

        if (set.UvSet != 0) {
            mapping.Set("uvSet", Whole(set.UvSet));
        }

        YamlSequence channels = new();

        foreach (var channel in set.Channels) {
            YamlMapping entry = new();

            entry.Set("usage", Text(channel.Usage));
            entry.Set("default", Numbers(channel.Default));
            channels.Add(entry);
        }

        mapping.Set("channels", channels);

        if (set.HeightNormal != LayerHeightNormal.Smooth) {
            mapping.Set("heightNormal", Text(set.HeightNormal.ToString()));
        }

        YamlSequence layers = new();

        foreach (var layer in set.Layers) {
            layers.Add(Write(layer));
        }

        mapping.Set("layers", layers);

        return mapping;
    }

    static YamlMapping Write(LayerAsset layer) {
        YamlMapping mapping = new();

        mapping.Set("id", Text(layer.Id));

        if (layer.Name.Length > 0) {
            mapping.Set("name", Text(layer.Name));
        }

        mapping.Set("kind", Text(layer.Kind.ToString()));

        if (layer.AnchorPoint.Length > 0) {
            mapping.Set("anchorPoint", Text(layer.AnchorPoint));
        }

        if (!layer.Enabled) {
            mapping.Set("enabled", new YamlScalar("false", YamlScalarStyle.Plain));
        }

        if (layer.Opacity is not 1f) {
            mapping.Set("opacity", Number(layer.Opacity));
        }

        if (layer.Blend != LayerBlendMode.Copy) {
            mapping.Set("blend", Text(layer.Blend.ToString()));
        }

        if (layer.Projection != LayerProjection.Uv) {
            mapping.Set("projection", Text(layer.Projection.ToString()));
        }

        // ⚠ Written whenever it is not y, whatever the projection is — #1032. A hand-written file may
        // carry an axis on a layer that is not planar, and a *writer* that decided such a value was
        // meaningless and dropped it would delete an author's line on a save they asked nothing of.
        // `Project`'s warning is what tells them it is doing nothing. The layers panel clears the
        // axis when it moves a layer off Planar, which is the same decision made where it is visible
        // and undoable.
        if (layer.PlanarAxis != LayerAxis.Y) {
            mapping.Set("axis", Text(layer.PlanarAxis.ToString()));
        }

        if (layer.Placement != new LayerPlacement()) {
            mapping.Set("placement", Write(layer.Placement));
        }

        if (layer.Decal is { } decal) {
            mapping.Set("decal", Write(decal));
        }

        if (layer.SurfacePath is { } surfacePath) {
            mapping.Set("path", Write(surfacePath));
        }

        if (layer.Channels.Count > 0) {
            YamlSequence channels = new() { Style = YamlCollectionStyle.Flow };

            foreach (var channel in layer.Channels) {
                channels.Add(Text(channel));
            }

            mapping.Set("channels", channels);
        }

        if (layer.Kind == LayerKind.Fill && layer.Fill != LayerFillSource.Constant) {
            mapping.Set("fill", Text(layer.Fill.ToString()));
        }

        if (layer.Values.Count > 0) {
            YamlMapping values = new();

            foreach (var (usage, colour) in layer.Values) {
                values.Set(usage, Numbers(colour));
            }

            mapping.Set("values", values);
        }

        if (layer.Textures.Count > 0) {
            YamlMapping textures = new();

            foreach (var (usage, asset) in layer.Textures) {
                textures.Set(usage, Text(asset));
            }

            mapping.Set("textures", textures);
        }

        if (layer.Graph.Length > 0) {
            mapping.Set("graph", Text(layer.Graph));
        }

        if (layer.Anchor.Anchor.Length > 0 || layer.Anchor.Channel.Length > 0) {
            mapping.Set("anchor", Write(layer.Anchor));
        }

        if (layer.Instance.Layer.Length > 0 || layer.Instance.Set.Length > 0) {
            YamlMapping instance = new() { Style = YamlCollectionStyle.Flow };

            if (layer.Instance.Set.Length > 0) {
                instance.Set("set", Text(layer.Instance.Set));
            }

            instance.Set("layer", Text(layer.Instance.Layer));
            mapping.Set("instance", instance);
        }

        WriteSources(mapping, "inputs", layer.Inputs);

        // ⚠ One of the two and never both — #1068. `filterNode` takes precedence when it is there,
        // and `LayerFilterKind.Levels` is zero, so a file carrying both would read as a compound and
        // *say* it was a Levels: two instructions where the compiler follows one.
        if (layer.Kind == LayerKind.Filter) {
            if (layer.FilterNode.Length > 0) {
                mapping.Set("filterNode", Text(layer.FilterNode));
            } else {
                mapping.Set("filter", Text(layer.Filter.ToString()));
            }
        }

        if (layer.Settings.Count > 0) {
            YamlMapping settings = new();

            foreach (var (port, value) in layer.Settings) {
                settings.Set(port, Numbers(value));
            }

            mapping.Set("settings", settings);
        }

        // ⚠ Beside `settings` and not inside it: numbers reach a port and these reach a *setting*,
        // and `Space/Triplanar` has an `Axis` that is both a setting name and a port name away from
        // one. `MaskEffectAsset` splits them for the same reason and this is that pair — #1079.
        if (layer.Texts.Count > 0) {
            YamlMapping texts = new();

            foreach (var (setting, value) in layer.Texts) {
                texts.Set(setting, Text(value));
            }

            mapping.Set("texts", texts);
        }

        if (layer.Mask.Source != LayerMaskSource.None
            || layer.Mask.Paint.Length > 0
            || layer.Mask.Layers.Count > 0
            || layer.Mask.Effects.Count > 0) {
            mapping.Set("mask", Write(layer.Mask));
        }

        if (layer.Paint.Length > 0) {
            mapping.Set("paint", Text(layer.Paint));
        }

        if (layer.Children.Count > 0) {
            YamlSequence children = new();

            foreach (var child in layer.Children) {
                children.Add(Write(child));
            }

            mapping.Set("children", children);
        }

        return mapping;
    }

    static YamlMapping Write(LayerPlacement placement) {
        LayerPlacement bare = new();
        YamlMapping mapping = [];

        foreach (var (key, value, fallback) in new[] {
            ("scale", placement.Scale, bare.Scale),
            ("rotation", placement.Rotation, bare.Rotation),
            ("offsetX", placement.OffsetX, bare.OffsetX),
            ("offsetY", placement.OffsetY, bare.OffsetY),
            ("sharpness", placement.Sharpness, bare.Sharpness)
        }) {
            if (value != fallback) {
                mapping.Set(key, Number(value));
            }
        }

        return mapping;
    }

    static LayerPlacement Placement(YamlMapping mapping, string path) {
        if (!mapping.TryGet("placement", out var node)) {
            return new();
        }

        LayerPlacement bare = new();
        var entries = Mapping(node, $"{path}.placement");

        return new() {
            Scale = Single(entries, "scale", bare.Scale, $"{path}.placement"),
            Rotation = Single(entries, "rotation", bare.Rotation, $"{path}.placement"),
            OffsetX = Single(entries, "offsetX", bare.OffsetX, $"{path}.placement"),
            OffsetY = Single(entries, "offsetY", bare.OffsetY, $"{path}.placement"),
            Sharpness = Single(entries, "sharpness", bare.Sharpness, $"{path}.placement")
        };
    }

    /// <summary>A decal's box, every member written so a person reading the file sees the whole of it.</summary>
    static YamlMapping Write(LayerDecal decal) {
        YamlMapping mapping = [];

        mapping.Set("centre", Numbers(decal.Centre));
        mapping.Set("facing", Numbers(decal.Facing));
        mapping.Set("up", Numbers(decal.Up));
        mapping.Set("width", Number(decal.Width));
        mapping.Set("height", Number(decal.Height));
        mapping.Set("depth", Number(decal.Depth));
        mapping.Set("angle", Number(decal.Angle));
        mapping.Set("fade", Number(decal.Fade));

        if (decal.Tiled) {
            mapping.Set("tiled", new YamlScalar("true", YamlScalarStyle.Plain));
        }

        if (decal.Sampling != LayerSampling.Bilinear) {
            mapping.Set("sampling", Text(decal.Sampling.ToString()));
        }

        if (decal.Box.Length > 0) {
            mapping.Set("box", Numbers(decal.Box));
        }

        if (decal.Warp.Length > 0) {
            mapping.Set("warp", Numbers(decal.Warp));
        }

        return mapping;
    }

    static LayerDecal ReadDecal(YamlNode node, string path) {
        var entries = Mapping(node, path);
        LayerDecal bare = new();

        return new() {
            Centre = Vector(entries, "centre", bare.Centre, path),
            Facing = Vector(entries, "facing", bare.Facing, path),
            Up = Vector(entries, "up", bare.Up, path),
            Width = Single(entries, "width", bare.Width, path),
            Height = Single(entries, "height", bare.Height, path),
            Depth = Single(entries, "depth", bare.Depth, path),
            Angle = Single(entries, "angle", bare.Angle, path),
            Fade = Single(entries, "fade", bare.Fade, path),
            Tiled = Flag(entries, "tiled", false, path),
            Sampling = Choice(entries, "sampling", LayerSampling.Bilinear, path),
            Box = entries.TryGet("box", out var box) ? Numbers(box, $"{path}.box") : [],
            Warp = entries.TryGet("warp", out var warp) ? Numbers(warp, $"{path}.warp") : []
        };
    }

    /// <summary>A path's curve: one flow sequence of seven numbers to a point, so a merge conflict is one point.</summary>
    static YamlMapping Write(LayerSurfacePath curve) {
        YamlMapping mapping = [];
        YamlSequence points = new();

        foreach (var point in curve.Points) {
            points.Add(
                Numbers(
                    [point.X, point.Y, point.Z, point.NormalX, point.NormalY, point.NormalZ, point.Pressure]
                )
            );
        }

        mapping.Set("points", points);
        mapping.Set("radius", Number(curve.Radius));
        mapping.Set("hardness", Number(curve.Hardness));
        mapping.Set("angle", Number(curve.Angle));

        if (curve.Ribbon) {
            mapping.Set("ribbon", new YamlScalar("true", YamlScalarStyle.Plain));
        }

        if (curve.Repeat != 0f) {
            mapping.Set("repeat", Number(curve.Repeat));
        }

        if (curve.Box.Length > 0) {
            mapping.Set("box", Numbers(curve.Box));
        }

        return mapping;
    }

    static LayerSurfacePath ReadPath(YamlNode node, string path) {
        var entries = Mapping(node, path);
        LayerSurfacePath bare = new();
        List<LayerSurfacePoint> points = [];

        if (entries.TryGet("points", out var listed)) {
            var index = 0;

            foreach (var item in Sequence(listed, $"{path}.points")) {
                var at = $"{path}.points[{index++}]";
                var numbers = Numbers(item, at);

                if (numbers.Length is not (6 or 7)) {
                    throw new YamlBindingException(
                        at,
                        $"a point is a place and a normal, six numbers, and a pressure if it has one; this has "
                        + $"{numbers.Length.ToString(CultureInfo.InvariantCulture)}."
                    );
                }

                points.Add(
                    new(
                        numbers[0],
                        numbers[1],
                        numbers[2],
                        numbers[3],
                        numbers[4],
                        numbers[5],
                        numbers.Length == 7 ? numbers[6] : 1f
                    )
                );
            }
        }

        return new() {
            Points = points,
            Radius = Single(entries, "radius", bare.Radius, path),
            Hardness = Single(entries, "hardness", bare.Hardness, path),
            Angle = Single(entries, "angle", bare.Angle, path),
            Ribbon = Flag(entries, "ribbon", false, path),
            Repeat = Single(entries, "repeat", 0f, path),
            Box = entries.TryGet("box", out var box) ? Numbers(box, $"{path}.box") : []
        };
    }

    /// <summary>Three numbers, refused as anything else, or the fallback when the key is missing.</summary>
    static float[] Vector(YamlMapping mapping, string key, float[] fallback, string path) {
        if (!mapping.TryGet(key, out var node)) {
            return fallback;
        }

        var numbers = Numbers(node, $"{path}.{key}");

        return numbers.Length == 3
            ? numbers
            : throw new YamlBindingException(
                $"{path}.{key}",
                $"expected three numbers and found {numbers.Length.ToString(CultureInfo.InvariantCulture)}."
            );
    }

    static YamlMapping Write(MaskAsset mask) {
        YamlMapping mapping = new();

        mapping.Set("source", Text(mask.Source.ToString()));

        if (mask.Source == LayerMaskSource.Constant) {
            mapping.Set("value", Number(mask.Value));
        }

        if (mask.Asset.Length > 0) {
            mapping.Set("asset", Text(mask.Asset));
        }

        if (mask.Anchor.Anchor.Length > 0 || mask.Anchor.Channel.Length > 0) {
            mapping.Set("anchor", Write(mask.Anchor));
        }

        if (mask.Generator.Length > 0) {
            mapping.Set("generator", Text(mask.Generator));
        }

        WriteGeneratorKnobs(mapping, mask.GeneratorValues, mask.GeneratorTexts);
        WriteInputs(mapping, "generatorInputs", mask.GeneratorInputs);

        if (mask.Map.Length > 0) {
            mapping.Set("map", Text(mask.Map));
        }

        if (mask.Lane != LayerMaskLane.Red) {
            mapping.Set("lane", Text(mask.Lane.ToString()));
        }

        if (mask.Paint.Length > 0) {
            mapping.Set("paint", Text(mask.Paint));
        }

        if (mask.Layers.Count > 0) {
            YamlSequence entries = new();

            foreach (var entry in mask.Layers) {
                entries.Add(Write(entry));
            }

            mapping.Set("layers", entries);
        }

        if (mask.Effects.Count > 0) {
            YamlSequence effects = new();

            foreach (var effect in mask.Effects) {
                effects.Add(Write(effect));
            }

            mapping.Set("effects", effects);
        }

        if (!mask.Enabled) {
            mapping.Set("enabled", new YamlScalar("false"));
        }

        return mapping;
    }

    static YamlMapping Write(MaskLayerAsset entry) {
        YamlMapping mapping = new();

        mapping.Set("source", Text(entry.Source.ToString()));

        if (entry.Source == LayerMaskSource.Constant) {
            mapping.Set("value", Number(entry.Value));
        }

        if (entry.Asset.Length > 0) {
            mapping.Set("asset", Text(entry.Asset));
        }

        if (entry.Anchor.Anchor.Length > 0 || entry.Anchor.Channel.Length > 0) {
            mapping.Set("anchor", Write(entry.Anchor));
        }

        if (entry.Generator.Length > 0) {
            mapping.Set("generator", Text(entry.Generator));
        }

        WriteGeneratorKnobs(mapping, entry.GeneratorValues, entry.GeneratorTexts);
        WriteInputs(mapping, "generatorInputs", entry.GeneratorInputs);

        if (entry.Map.Length > 0) {
            mapping.Set("map", Text(entry.Map));
        }

        if (entry.Lane != LayerMaskLane.Red) {
            mapping.Set("lane", Text(entry.Lane.ToString()));
        }

        if (entry.Paint.Length > 0) {
            mapping.Set("paint", Text(entry.Paint));
        }

        if (entry.Blend != LayerBlendMode.Copy) {
            mapping.Set("blend", Text(entry.Blend.ToString()));
        }

        if (entry.Opacity is < 1f or > 1f) {
            mapping.Set("opacity", Number(entry.Opacity));
        }

        if (!entry.Enabled) {
            mapping.Set("enabled", new YamlScalar("false"));
        }

        if (entry.Kept) {
            mapping.Set("kept", new YamlScalar("true"));
        }

        return mapping;
    }

    static YamlMapping Write(MaskEffectAsset effect) {
        YamlMapping mapping = new();

        mapping.Set("node", Text(effect.Node));

        if (!effect.Enabled) {
            mapping.Set("enabled", new YamlScalar("false"));
        }

        if (effect.Values.Count > 0) {
            YamlMapping values = new();

            foreach (var (port, value) in effect.Values) {
                values.Set(port, Numbers(value));
            }

            mapping.Set("values", values);
        }

        if (effect.Texts.Count > 0) {
            YamlMapping texts = new();

            foreach (var (setting, value) in effect.Texts) {
                texts.Set(setting, Text(value));
            }

            mapping.Set("texts", texts);
        }

        WriteSources(mapping, "inputs", effect.Inputs);

        return mapping;
    }

    /// <summary>A reference to an anchor point, as its name and the channel it reads.</summary>
    static YamlMapping Write(AnchorReference reference) {
        YamlMapping mapping = new() { Style = YamlCollectionStyle.Flow };

        mapping.Set("name", Text(reference.Anchor));

        if (reference.Channel.Length > 0) {
            mapping.Set("channel", Text(reference.Channel));
        }

        return mapping;
    }

    static void WriteInputs(YamlMapping mapping, string key, Dictionary<string, AnchorReference> inputs) {
        if (inputs.Count == 0) {
            return;
        }

        YamlMapping ports = new();

        foreach (var (port, reference) in inputs) {
            ports.Set(port, Write(reference));
        }

        mapping.Set(key, ports);
    }

    /// <summary>Where each image input reads, by port: its source and the members that source reads, and nothing else.</summary>
    static void WriteSources(YamlMapping mapping, string key, Dictionary<string, LayerInputAsset> inputs) {
        if (inputs.Count == 0) {
            return;
        }

        YamlMapping ports = new();

        foreach (var (port, input) in inputs) {
            YamlMapping entry = new();

            entry.Set("source", Text(input.Source.ToString()));

            switch (input.Source) {
                case LayerInputSource.Beneath when input.Channel.Length > 0:
                    entry.Set("channel", Text(input.Channel));

                    break;

                case LayerInputSource.Anchor:
                    entry.Set("anchor", Write(input.Anchor));

                    break;

                case LayerInputSource.Generator: {
                    entry.Set("generator", Text(input.Generator));

                    if (input.Values.Count > 0) {
                        YamlMapping values = new();

                        foreach (var (name, value) in input.Values) {
                            values.Set(name, Numbers(value));
                        }

                        entry.Set("values", values);
                    }

                    if (input.Texts.Count > 0) {
                        YamlMapping texts = new();

                        foreach (var (name, value) in input.Texts) {
                            texts.Set(name, Text(value));
                        }

                        entry.Set("texts", texts);
                    }

                    break;
                }

                case LayerInputSource.Texture:
                    entry.Set("asset", Text(input.Asset));

                    break;

                case LayerInputSource.Bake:
                    entry.Set("map", Text(input.Map));

                    break;
            }

            ports.Set(port, entry);
        }

        mapping.Set(key, ports);
    }

    static Dictionary<string, LayerInputAsset> ReadSources(YamlMapping mapping, string key, string path) {
        Dictionary<string, LayerInputAsset> inputs = [];

        if (!mapping.TryGet(key, out var node)) {
            return inputs;
        }

        foreach (var (port, value) in Mapping(node, $"{path}.{key}")) {
            var where = $"{path}.{key}.{port}";
            var entry = Mapping(value, where);

            inputs[port] = new() {
                Source = Choice(entry, "source", LayerInputSource.Beneath, where),
                Channel = String(entry, "channel", where),
                Anchor = ReadAnchor(entry, "anchor", where),
                Generator = String(entry, "generator", where),
                Values = Colours(entry, "values", where),
                Texts = Strings(entry, "texts", where),
                Asset = String(entry, "asset", where),
                Map = String(entry, "map", where)
            };
        }

        return inputs;
    }

    static AnchorReference ReadAnchor(YamlMapping mapping, string key, string path) {
        if (!mapping.TryGet(key, out var node)) {
            return new();
        }

        return ReadReference(node, $"{path}.{key}");
    }

    static AnchorReference ReadReference(YamlNode node, string path) {
        var reference = Mapping(node, path);

        return new() { Anchor = String(reference, "name", path), Channel = String(reference, "channel", path) };
    }

    static Dictionary<string, AnchorReference> ReadInputs(YamlMapping mapping, string key, string path) {
        Dictionary<string, AnchorReference> inputs = [];

        if (!mapping.TryGet(key, out var node)) {
            return inputs;
        }

        foreach (var (port, entry) in Mapping(node, $"{path}.{key}")) {
            inputs[port] = ReadReference(entry, $"{path}.{key}.{port}");
        }

        return inputs;
    }

    static TextureSetAsset ReadSet(YamlNode node, string path) {
        var mapping = Mapping(node, path);

        List<ChannelAsset> channels = [];

        if (mapping.TryGet("channels", out var declared)) {
            var index = 0;

            foreach (var item in Sequence(declared, $"{path}.channels")) {
                var channel = Mapping(item, $"{path}.channels[{index}]");

                channels.Add(new() {
                    Usage = String(channel, "usage", $"{path}.channels[{index}]"),
                    Default = Colour(channel, "default", $"{path}.channels[{index}]")
                });

                index++;
            }
        }

        List<LayerAsset> layers = [];

        if (mapping.TryGet("layers", out var stacked)) {
            var index = 0;

            foreach (var item in Sequence(stacked, $"{path}.layers")) {
                layers.Add(ReadLayer(item, $"{path}.layers[{index++}]"));
            }
        }

        return new() {
            Name = String(mapping, "name", path),
            Mesh = String(mapping, "mesh", path),
            UvSet = Integer(mapping, "uvSet", 0, path, unsigned: true),
            Channels = channels,
            HeightNormal = Choice(mapping, "heightNormal", LayerHeightNormal.Smooth, path),
            Layers = layers
        };
    }

    static LayerAsset ReadLayer(YamlNode node, string path) {
        var mapping = Mapping(node, path);

        List<string> channels = [];

        if (mapping.TryGet("channels", out var restricted)) {
            foreach (var item in Sequence(restricted, $"{path}.channels")) {
                channels.Add(Scalar(item, $"{path}.channels"));
            }
        }

        List<LayerAsset> children = [];

        if (mapping.TryGet("children", out var nested)) {
            var index = 0;

            foreach (var item in Sequence(nested, $"{path}.children")) {
                children.Add(ReadLayer(item, $"{path}.children[{index++}]"));
            }
        }

        return new() {
            Id = String(mapping, "id", path),
            Name = String(mapping, "name", path),
            AnchorPoint = String(mapping, "anchorPoint", path),
            Kind = Choice(mapping, "kind", LayerKind.Fill, path),
            Enabled = Flag(mapping, "enabled", true, path),
            Opacity = Single(mapping, "opacity", 1f, path),
            Blend = Choice(mapping, "blend", LayerBlendMode.Copy, path),
            Projection = Choice(mapping, "projection", LayerProjection.Uv, path),
            PlanarAxis = Choice(mapping, "axis", LayerAxis.Y, path),
            Placement = Placement(mapping, path),
            Decal = mapping.TryGet("decal", out var decal) ? ReadDecal(decal, $"{path}.decal") : null,
            SurfacePath = mapping.TryGet("path", out var curve) ? ReadPath(curve, $"{path}.path") : null,
            Channels = channels,
            Fill = Choice(mapping, "fill", LayerFillSource.Constant, path),
            Values = Colours(mapping, "values", path),
            Textures = Strings(mapping, "textures", path),
            Graph = String(mapping, "graph", path),
            Anchor = ReadAnchor(mapping, "anchor", path),
            Inputs = ReadSources(mapping, "inputs", path),
            Filter = Choice(mapping, "filter", LayerFilterKind.Levels, path),
            FilterNode = String(mapping, "filterNode", path),
            Settings = Colours(mapping, "settings", path),
            Texts = Strings(mapping, "texts", path),
            Mask = mapping.TryGet("mask", out var mask) ? ReadMask(mask, $"{path}.mask") : new(),
            Paint = String(mapping, "paint", path),
            Children = children,
            Instance = mapping.TryGet("instance", out var instance) ? ReadInstance(instance, $"{path}.instance") : new()
        };
    }

    static InstanceReference ReadInstance(YamlNode node, string path) {
        var reference = Mapping(node, path);

        return new() { Set = String(reference, "set", path), Layer = String(reference, "layer", path) };
    }

    static MaskAsset ReadMask(YamlNode node, string path) {
        var mapping = Mapping(node, path);
        List<MaskLayerAsset> entries = [];
        List<MaskEffectAsset> effects = [];

        if (mapping.TryGet("layers", out var stacked)) {
            foreach (var item in Sequence(stacked, $"{path}.layers")) {
                entries.Add(ReadMaskLayer(item, $"{path}.layers"));
            }
        }

        if (mapping.TryGet("effects", out var adjusted)) {
            foreach (var item in Sequence(adjusted, $"{path}.effects")) {
                effects.Add(ReadMaskEffect(item, $"{path}.effects"));
            }
        }

        return new() {
            Source = Choice(mapping, "source", LayerMaskSource.None, path),
            Value = Single(mapping, "value", 1f, path),
            Asset = String(mapping, "asset", path),
            Anchor = ReadAnchor(mapping, "anchor", path),
            Generator = String(mapping, "generator", path),
            GeneratorValues = Colours(mapping, "generatorValues", path),
            GeneratorTexts = Strings(mapping, "generatorTexts", path),
            GeneratorInputs = ReadInputs(mapping, "generatorInputs", path),
            Map = String(mapping, "map", path),
            Lane = Choice(mapping, "lane", LayerMaskLane.Red, path),
            Paint = String(mapping, "paint", path),
            Layers = entries,
            Effects = effects,
            Enabled = Flag(mapping, "enabled", true, path)
        };
    }

    static void WriteGeneratorKnobs(
        YamlMapping mapping,
        Dictionary<string, float[]> numbers,
        Dictionary<string, string> settings
    ) {
        if (numbers.Count > 0) {
            YamlMapping values = new();

            foreach (var (port, value) in numbers) {
                values.Set(port, Numbers(value));
            }

            mapping.Set("generatorValues", values);
        }

        if (settings.Count > 0) {
            YamlMapping texts = new();

            foreach (var (setting, value) in settings) {
                texts.Set(setting, Text(value));
            }

            mapping.Set("generatorTexts", texts);
        }
    }

    static MaskLayerAsset ReadMaskLayer(YamlNode node, string path) {
        var mapping = Mapping(node, path);

        return new() {
            Source = Choice(mapping, "source", LayerMaskSource.Constant, path),
            Value = Single(mapping, "value", 1f, path),
            Asset = String(mapping, "asset", path),
            Anchor = ReadAnchor(mapping, "anchor", path),
            Generator = String(mapping, "generator", path),
            GeneratorValues = Colours(mapping, "generatorValues", path),
            GeneratorTexts = Strings(mapping, "generatorTexts", path),
            GeneratorInputs = ReadInputs(mapping, "generatorInputs", path),
            Map = String(mapping, "map", path),
            Lane = Choice(mapping, "lane", LayerMaskLane.Red, path),
            Paint = String(mapping, "paint", path),
            Blend = Choice(mapping, "blend", LayerBlendMode.Copy, path),
            Opacity = Single(mapping, "opacity", 1f, path),
            Enabled = Flag(mapping, "enabled", true, path),
            Kept = Flag(mapping, "kept", false, path)
        };
    }

    static MaskEffectAsset ReadMaskEffect(YamlNode node, string path) {
        var mapping = Mapping(node, path);

        return new() {
            Node = String(mapping, "node", path),
            Enabled = Flag(mapping, "enabled", true, path),
            Values = Colours(mapping, "values", path),
            Texts = Strings(mapping, "texts", path),
            Inputs = ReadSources(mapping, "inputs", path)
        };
    }

    /// <summary>A string, quoted only where YAML needs it.</summary>
    /// <remarks>
    ///     ⚠ <b>An empty string is the one that has to be quoted</b>, because a plain empty scalar
    ///     reads back as a null node rather than as <c>""</c>. Everything a stack writes as text is a
    ///     name, a usage, an asset path or an enum member, none of which YAML would take for a number
    ///     or a keyword.
    /// </remarks>
    static YamlScalar Text(string value) =>
        new(value, value.Length == 0 ? YamlScalarStyle.SingleQuoted : YamlScalarStyle.Plain);

    /// <summary>A number, unquoted, round-tripping.</summary>
    /// <remarks>
    ///     ⚠ <b><c>Plain</c> rather than the emitter's own choice, which quotes.</b> A
    ///     <c>.vxlayers</c> is a file people read and merge, and <c>opacity: '0.6'</c> beside
    ///     <c>default: ['0.5', '0.5']</c> is a file that looks like it holds strings. <c>R</c> is what
    ///     makes the read back exact.
    /// </remarks>
    static YamlScalar Number(float value) =>
        new(value.ToString("R", CultureInfo.InvariantCulture), YamlScalarStyle.Plain);

    static YamlScalar Whole(int value) =>
        new(value.ToString(CultureInfo.InvariantCulture), YamlScalarStyle.Plain);

    static YamlSequence Numbers(float[] values) {
        YamlSequence sequence = new() { Style = YamlCollectionStyle.Flow };

        foreach (var value in values) {
            sequence.Add(Number(value));
        }

        return sequence;
    }

    static YamlMapping Mapping(YamlNode node, string path) =>
        node as YamlMapping
        ?? throw new YamlBindingException(path, $"expected a mapping and found {node.GetType().Name}.");

    static YamlSequence Sequence(YamlNode node, string path) =>
        node as YamlSequence
        ?? throw new YamlBindingException(path, $"expected a sequence and found {node.GetType().Name}.");

    static string Scalar(YamlNode node, string path) =>
        (node as YamlScalar)?.Value
        ?? throw new YamlBindingException(path, $"expected a scalar and found {node.GetType().Name}.");

    static string String(YamlMapping mapping, string key, string path) =>
        mapping.TryGet(key, out var node) ? Scalar(node, $"{path}.{key}") : "";

    static int Integer(YamlMapping mapping, string key, int fallback, string path, bool unsigned = false) {
        if (!mapping.TryGet(key, out var node)) {
            return fallback;
        }

        var text = Scalar(node, $"{path}.{key}");

        if (unsigned && uint.TryParse(text, CultureInfo.InvariantCulture, out var word)) {
            return unchecked((int)word);
        }

        if (!unsigned && int.TryParse(text, CultureInfo.InvariantCulture, out var value)) {
            return value;
        }

        throw new YamlBindingException($"{path}.{key}", $"'{text}' is not a whole number.");
    }

    static float Single(YamlMapping mapping, string key, float fallback, string path) {
        if (!mapping.TryGet(key, out var node)) {
            return fallback;
        }

        var text = Scalar(node, $"{path}.{key}");

        return float.TryParse(text, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new YamlBindingException($"{path}.{key}", $"'{text}' is not a number.");
    }

    static bool Flag(YamlMapping mapping, string key, bool fallback, string path) {
        if (!mapping.TryGet(key, out var node)) {
            return fallback;
        }

        var text = Scalar(node, $"{path}.{key}");

        return text switch {
            "true" or "True" or "yes" => true,
            "false" or "False" or "no" => false,
            _ => throw new YamlBindingException($"{path}.{key}", $"'{text}' is not true or false.")
        };
    }

    /// <summary>An enum member by name, refused rather than defaulted when this build lacks it.</summary>
    /// <remarks>
    ///     ⚠ <b>The one place a silent default would be a picture rather than an error.</b> A stack
    ///     written by a build with a seventeenth blend mode, read here, would composite every layer
    ///     that used it as a <c>Copy</c> — and nothing anywhere would say so.
    /// </remarks>
    static T Choice<T>(YamlMapping mapping, string key, T fallback, string path) where T : struct, Enum {
        if (!mapping.TryGet(key, out var node)) {
            return fallback;
        }

        var text = Scalar(node, $"{path}.{key}");

        return Enum.TryParse<T>(text, out var value) && Enum.IsDefined(value)
            ? value
            : throw new YamlBindingException(
                $"{path}.{key}",
                $"'{text}' is not one of {string.Join(", ", Enum.GetNames<T>())}. This stack was written by a "
                + "build that knows something this one does not."
            );
    }

    static Dictionary<string, float[]> Colours(YamlMapping mapping, string key, string path) {
        Dictionary<string, float[]> values = [];

        if (!mapping.TryGet(key, out var node)) {
            return values;
        }

        foreach (var (name, entry) in Mapping(node, $"{path}.{key}")) {
            values[name] = Numbers(entry, $"{path}.{key}.{name}");
        }

        return values;
    }

    static Dictionary<string, string> Strings(YamlMapping mapping, string key, string path) {
        Dictionary<string, string> values = [];

        if (!mapping.TryGet(key, out var node)) {
            return values;
        }

        foreach (var (name, entry) in Mapping(node, $"{path}.{key}")) {
            values[name] = Scalar(entry, $"{path}.{key}.{name}");
        }

        return values;
    }

    static float[] Colour(YamlMapping mapping, string key, string path) =>
        mapping.TryGet(key, out var node) ? Numbers(node, $"{path}.{key}") : [0f, 0f, 0f, 1f];

    static float[] Numbers(YamlNode node, string path) {
        List<float> values = [];

        foreach (var item in Sequence(node, path)) {
            var text = Scalar(item, path);

            values.Add(
                float.TryParse(text, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : throw new YamlBindingException(path, $"'{text}' is not a number.")
            );
        }

        return [.. values];
    }
}
