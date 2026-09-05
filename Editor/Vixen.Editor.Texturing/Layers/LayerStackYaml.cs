// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Yaml;

namespace Vixen.Editor.Texturing.Layers;

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
static class LayerStackYaml {
    /// <summary>The stack as YAML.</summary>
    /// <param name="stack">The stack.</param>
    /// <returns>The text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stack" /> is null.</exception>
    public static string Write(LayerStackAsset stack) {
        ArgumentNullException.ThrowIfNull(stack);

        YamlMapping root = new();

        root.Set("version", Whole(stack.Version));
        root.Set("name", Text(stack.Name));
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
    /// <param name="text">What <see cref="Write" /> wrote.</param>
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
            BaseWidth = Integer(root, "baseWidth", 1024, ""),
            BaseHeight = Integer(root, "baseHeight", 1024, ""),
            Seed = (uint)Integer(root, "seed", 0, "", unsigned: true),
            Sets = sets
        };
    }

    static YamlMapping Write(TextureSetAsset set) {
        YamlMapping mapping = new();

        mapping.Set("name", Text(set.Name));

        YamlSequence channels = new();

        foreach (var channel in set.Channels) {
            YamlMapping entry = new();

            entry.Set("usage", Text(channel.Usage));
            entry.Set("default", Numbers(channel.Default));
            channels.Add(entry);
        }

        mapping.Set("channels", channels);

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

        if (layer.Kind == LayerKind.Filter) {
            mapping.Set("filter", Text(layer.Filter.ToString()));
        }

        if (layer.Settings.Count > 0) {
            YamlMapping settings = new();

            foreach (var (port, value) in layer.Settings) {
                settings.Set(port, Numbers(value));
            }

            mapping.Set("settings", settings);
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

    static YamlMapping Write(MaskAsset mask) {
        YamlMapping mapping = new();

        mapping.Set("source", Text(mask.Source.ToString()));

        if (mask.Source == LayerMaskSource.Constant) {
            mapping.Set("value", Number(mask.Value));
        }

        if (mask.Asset.Length > 0) {
            mapping.Set("asset", Text(mask.Asset));
        }

        if (mask.Anchor.Length > 0) {
            mapping.Set("anchor", Text(mask.Anchor));
        }

        if (mask.Generator.Length > 0) {
            mapping.Set("generator", Text(mask.Generator));
        }

        if (mask.Map.Length > 0) {
            mapping.Set("map", Text(mask.Map));
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

        if (entry.Anchor.Length > 0) {
            mapping.Set("anchor", Text(entry.Anchor));
        }

        if (entry.Generator.Length > 0) {
            mapping.Set("generator", Text(entry.Generator));
        }

        if (entry.Map.Length > 0) {
            mapping.Set("map", Text(entry.Map));
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

        return mapping;
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

        return new() { Name = String(mapping, "name", path), Channels = channels, Layers = layers };
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
            Kind = Choice(mapping, "kind", LayerKind.Fill, path),
            Enabled = Flag(mapping, "enabled", true, path),
            Opacity = Single(mapping, "opacity", 1f, path),
            Blend = Choice(mapping, "blend", LayerBlendMode.Copy, path),
            Projection = Choice(mapping, "projection", LayerProjection.Uv, path),
            Channels = channels,
            Fill = Choice(mapping, "fill", LayerFillSource.Constant, path),
            Values = Colours(mapping, "values", path),
            Textures = Strings(mapping, "textures", path),
            Graph = String(mapping, "graph", path),
            Filter = Choice(mapping, "filter", LayerFilterKind.Levels, path),
            Settings = Colours(mapping, "settings", path),
            Mask = mapping.TryGet("mask", out var mask) ? ReadMask(mask, $"{path}.mask") : new(),
            Paint = String(mapping, "paint", path),
            Children = children
        };
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
            Anchor = String(mapping, "anchor", path),
            Generator = String(mapping, "generator", path),
            Map = String(mapping, "map", path),
            Paint = String(mapping, "paint", path),
            Layers = entries,
            Effects = effects
        };
    }

    static MaskLayerAsset ReadMaskLayer(YamlNode node, string path) {
        var mapping = Mapping(node, path);

        return new() {
            Source = Choice(mapping, "source", LayerMaskSource.Constant, path),
            Value = Single(mapping, "value", 1f, path),
            Asset = String(mapping, "asset", path),
            Anchor = String(mapping, "anchor", path),
            Generator = String(mapping, "generator", path),
            Map = String(mapping, "map", path),
            Paint = String(mapping, "paint", path),
            Blend = Choice(mapping, "blend", LayerBlendMode.Copy, path),
            Opacity = Single(mapping, "opacity", 1f, path),
            Enabled = Flag(mapping, "enabled", true, path)
        };
    }

    static MaskEffectAsset ReadMaskEffect(YamlNode node, string path) {
        var mapping = Mapping(node, path);

        return new() {
            Node = String(mapping, "node", path),
            Enabled = Flag(mapping, "enabled", true, path),
            Values = Colours(mapping, "values", path),
            Texts = Strings(mapping, "texts", path)
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
