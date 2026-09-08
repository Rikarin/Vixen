// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     Every setting the compiler refuses a name for offers exactly the names it refuses against —
///     #1013, #1017.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the load-bearing half of <c>[Setting(AcceptedFrom = …)]</c>, and the dropdown
///         is the visible one.</b> A picker that cannot reach a value the compiler accepts is worse
///         than the text box it replaced, because the text box could reach it — and that failure is
///         silent in the one direction nobody looks. So the assertion is not "the list is right"; it
///         is "the list a picker offers and the set a refusal enumerates are the same set".
///     </para>
///     <para>
///         ⚠ <b>Nothing here transcribes a set, which is the whole point.</b> The expected values
///         come out of the compiler's own refusal message — <c>TextureSettings.Enum&lt;T&gt;</c>
///         builds it from <c>Enum.GetNames&lt;T&gt;()</c> — so a member added to
///         <c>TextureBlendMode</c> changes both sides at once and this stays green, while a setting
///         declared against the <em>wrong</em> enum, or against a literal list, goes red. A roll call
///         with its own copy of the nineteen lists would be a third transcription and would go red
///         for the one reason that does not matter.
///     </para>
///     <para>
///         ⚠ <b>The settings are discovered rather than listed, so the count is the instrument.</b>
///         A walk that found nothing would pass every assertion it made; <see cref="Enumerated" />
///         is what says it walked. Read it as "at least this many settings are enum-backed today",
///         not as a census — a new node with an enum setting raises it and is welcome to.
///     </para>
/// </remarks>
public class TextureSettingChoiceTests {
    /// <summary>How many settings the sweep must find before its silence means anything.</summary>
    /// <remarks>
    ///     Twenty-three <c>TextureSettings.Enum</c> call sites, plus the two nodes that refuse by
    ///     walking a list of their own — <c>Source/Mesh Map</c>'s nine measurements and
    ///     <c>Output/Output</c>'s nine usages. Both of those lists are now read off the declaration,
    ///     so for them the comparison is an identity and they are here to be counted rather than
    ///     compared.
    /// </remarks>
    const int Enumerated = 25;

    /// <summary>A name no enum in this library holds, which is what provokes the refusal.</summary>
    const string Nonsense = "mulitply";

    /// <summary>What a picker offers is what the compiler refuses against, for every setting.</summary>
    [Fact]
    public void Every_refused_setting_offers_exactly_the_names_its_refusal_enumerates() {
        var found = 0;

        foreach (var (definition, setting, refusal) in Refusals()) {
            found++;

            Assert.True(
                setting.IsChoice,
                $"'{definition.Path}' refuses a name for '{setting.Name}' and states no accepted "
                + "values, so the node inspector draws it as a text box in which a typo is a "
                + "diagnostic after the fact. Declare AcceptedFrom = typeof(the enum it is read as)."
            );

            // ⚠ Sorted, because the two orders are deliberately different and only the membership is
            // a claim. `Enum.GetNames` — which the refusal is built from — sorts by the underlying
            // value read as unsigned, and the picker offers the order the enum was written in; they
            // part company on `TextureResampleSize`, whose members run -2 to 2. A sequence equality
            // here would have been an assertion about `Enum.GetNames`' implementation, which is not
            // what anybody wants held still.
            var withheld = Withheld
                .Where(row => row.Path == definition.Path && row.Setting == setting.Name)
                .Select(row => row.Value)
                .ToImmutableArray();

            Assert.Equal(
                Offered(refusal).Where(name => !withheld.Contains(name)).Order(StringComparer.Ordinal),
                setting.Accepted.Order(StringComparer.Ordinal)
            );
        }

        Assert.True(
            found >= Enumerated,
            $"Only {found} settings were refused a name, and there are at least {Enumerated}. Either a "
            + "node stopped compiling before it read its setting — in which case this walk is asserting "
            + "nothing about it — or the refusal's wording changed and Offered no longer parses it."
        );
    }

    /// <summary>Every value a picker offers is one the compiler then accepts.</summary>
    /// <remarks>
    ///     ⚠ <b>The other direction, and it is not the same assertion.</b> The roll call above holds
    ///     the offered list to the refusal's list; this holds it to the parser, which is a different
    ///     piece of code — <c>Enum.TryParse</c> plus <c>Enum.IsDefined</c>. A picker whose options
    ///     were the names of an enum's members but written in a way <c>TryParse</c> declines would
    ///     satisfy the first test and produce a node that refuses whatever the author picks.
    /// </remarks>
    [Fact]
    public void Every_value_a_picker_offers_compiles_without_a_refusal() {
        var checkedValues = 0;

        foreach (var (definition, setting, _) in Refusals()) {
            foreach (var value in setting.Accepted) {
                var compilation = Compiler().Compile(One(definition.Path, setting.Name, value));

                Assert.DoesNotContain(
                    compilation.Diagnostics,
                    diagnostic => diagnostic.Id == "TG0010" && Names(diagnostic.Message, setting.Name)
                );

                checkedValues++;
            }
        }

        Assert.True(checkedValues >= 100, $"Only {checkedValues} offered values were compiled.");
    }

    /// <summary>A generated list is the enum as written, not the enum as <c>Enum.GetNames</c> sorts it.</summary>
    /// <remarks>
    ///     ⚠ <b>The one enum in the library where the two differ, which is why this is a test rather
    ///     than a remark.</b> <c>TextureResampleSize</c> is <c>Quadruple = -2</c> through
    ///     <c>Quarter = 2</c> — the value is the mip-level offset, so the sign carries meaning — and
    ///     <c>Enum.GetNames</c>, sorting by the unsigned bit pattern, answers
    ///     <c>Same, Half, Quarter, Quadruple, Double</c>. A picker offering that would be offering an
    ///     artefact of two's complement as an ordering; a reader of the dropdown expects the sizes to
    ///     run one way. <see cref="Every_refused_setting_offers_exactly_the_names_its_refusal_enumerates" />
    ///     compares the two as sets for exactly this reason, which leaves the order unheld — so it is
    ///     held here.
    /// </remarks>
    [Fact]
    public void A_generated_list_is_offered_in_the_order_the_enum_is_written() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        var setting = registry.Get("Space/Resample").Setting("Size");

        Assert.NotNull(setting);
        Assert.Equal(["Quadruple", "Double", "Same", "Half", "Quarter"], setting.Accepted);
    }

    /// <summary>The settings that deliberately offer fewer names than their refusal enumerates.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A narrowing is a claim, and this is the list of them so that
    ///         <see cref="Every_refused_setting_offers_exactly_the_names_its_refusal_enumerates" />
    ///         stays an equality rather than becoming a subset.</b> "Offered ⊆ refused" would be
    ///         satisfied by a picker that offered nothing at all, which is the weaker assertion in
    ///         the direction that matters — a value the compiler accepts and no dropdown can reach
    ///         is exactly what #1013 was filed about.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Each row is held to a real refusal</b> by
    ///         <see cref="A_withheld_value_is_one_the_node_refuses" />, so a value cannot be dropped
    ///         from a picker on somebody's opinion that it is unlikely.
    ///     </para>
    /// </remarks>
    internal static ImmutableArray<(string Path, string Setting, string Value)> Withheld { get; } = [
        // `Transform2D.rvn`, `Crop.rvn` and `Bitmap.rvn` each compare `filter` against 0 and
        // interpolate for everything else, so a `Box` would be a bilinear read drawn under the name
        // of a box filter. A box needs a minification ratio, which only `Space/Resample` has.
        ("Space/Transform 2D", "Filter", "Box"),
        ("Space/Crop", "Filter", "Box"),
        ("Source/Bitmap", "Filter", "Box")
    ];

    /// <summary>Settings a node needs written before it compiles as far as the one under test.</summary>
    static ImmutableArray<(string Path, string Setting, string Value)> Prerequisites { get; } = [
        ("Source/Bitmap", "Source", "Assets/Textures/anything.png")
    ];

    /// <summary><see cref="Withheld" />, as rows a theory can take.</summary>
    public static TheoryData<string, string, string> Narrowed {
        get {
            TheoryData<string, string, string> rows = [];

            foreach (var (path, setting, value) in Withheld) {
                rows.Add(path, setting, value);
            }

            return rows;
        }
    }

    /// <summary>A value withheld from a picker is one the node refuses when it is written by hand.</summary>
    /// <remarks>
    ///     ⚠ <b>The graph is wired, which is the whole point of this test.</b> A node whose image
    ///     input is missing reports that and returns before it reads its own settings, so
    ///     <see cref="One" /> — which connects nothing — reaches the <c>TextureSettings.Enum</c>
    ///     parse at the top of a node and nothing below it. A narrowing check run on an unconnected
    ///     node would pass whether or not the refusal existed.
    /// </remarks>
    /// <param name="path">The node.</param>
    /// <param name="setting">The setting.</param>
    /// <param name="value">The value the picker withholds.</param>
    [Theory]
    [MemberData(nameof(Narrowed))]
    public void A_withheld_value_is_one_the_node_refuses(string path, string setting, string value) {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        var definition = registry.Get(path);

        Assert.DoesNotContain(value, definition.Setting(setting)!.Accepted);

        var compilation = Compiler().Compile(Wired(path, setting, value));

        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Id == "TG0010" && Names(diagnostic.Message, setting)
        );
    }

    /// <summary>The same graph with the setting left alone, so the refusal above is about the value.</summary>
    /// <param name="path">The node.</param>
    /// <param name="setting">The setting.</param>
    /// <param name="value">Unused — the row is shared with <see cref="A_withheld_value_is_one_the_node_refuses" />.</param>
    [Theory]
    [MemberData(nameof(Narrowed))]
    public void The_same_graph_compiles_when_the_withheld_value_is_not_written(
        string path,
        string setting,
        string value
    ) {
        _ = value;

        var compilation = Compiler().Compile(Wired(path, setting, null));

        Assert.DoesNotContain(
            compilation.Diagnostics,
            diagnostic => diagnostic.Id == "TG0010" && Names(diagnostic.Message, setting)
        );
    }

    /// <summary>A graph in which every input port of one node is fed, so the node compiles through.</summary>
    /// <param name="path">The node.</param>
    /// <param name="setting">The setting to write.</param>
    /// <param name="value">The value, or <c>null</c> to leave the setting at its default.</param>
    /// <returns>The graph.</returns>
    static NodeGraphModel Wired(string path, string setting, string? value) {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        NodeGraphModel graph = new();
        var node = graph.Add(path);

        if (value is not null) {
            node.SetText(setting, value);
        }

        // ⚠ A node can refuse and return above the setting this theory is about. `Source/Bitmap`
        // reads its asset reference first and refuses an empty one, so without a reference the Box
        // check below it is never reached and the theory would be green for the wrong reason.
        foreach (var (owner, required, filled) in Prerequisites) {
            if (owner == path) {
                node.SetText(required, filled);
            }
        }

        // An unwired image input is reported before a node reads its own settings, so without this
        // the graph never reaches the refusal the theory is about.
        foreach (var port in registry.Get(path).Ports) {
            if (port is { Direction: PortDirection.Input, Kind: PortKind.Image }) {
                graph.Connect(new(graph.Add("Source/Noise").Id, "Out"), new(node.Id, port.Name));
            }
        }

        return graph;
    }

    /// <summary>Each setting whose value the compiler refuses, with the sentence it refused it in.</summary>
    /// <remarks>
    ///     ⚠ <b>Discovered by provocation rather than by reading the source.</b> Whether a setting is
    ///     read as an enum is a fact about a method body, which nothing can reflect over — so the
    ///     walk sets every text setting to a name nothing accepts and keeps the ones that complain.
    ///     A setting that is genuinely a name — an asset reference, a menu path — says nothing and is
    ///     correctly skipped.
    /// </remarks>
    static IEnumerable<(NodeTypeDefinition Definition, SettingDefinition Setting, string Refusal)> Refusals() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        foreach (var definition in registry.Types) {
            foreach (var setting in definition.Settings) {
                if (setting.Kind != SettingKind.Text) {
                    continue;
                }

                var compilation = Compiler().Compile(One(definition.Path, setting.Name, Nonsense));

                foreach (var diagnostic in compilation.Diagnostics) {
                    if (diagnostic.Id == "TG0010" && Names(diagnostic.Message, setting.Name)) {
                        yield return (definition, setting, diagnostic.Message);

                        break;
                    }
                }
            }
        }
    }

    /// <summary>The names a refusal enumerates, in the order it enumerates them.</summary>
    /// <param name="refusal">The message.</param>
    /// <returns>The names, or empty when the sentence is not the shape this reads.</returns>
    /// <remarks>
    ///     ⚠ <b>Cut at the first full stop that ends the list, because one refusal says more
    ///     afterwards.</b> <c>Source/Mesh Map</c>'s adds a sentence about what a mesh map is bound
    ///     by; no member name of any of these enums contains a full stop, so the first one is the end
    ///     of the list wherever the sentence goes on.
    /// </remarks>
    static ImmutableArray<string> Offered(string refusal) {
        const string marker = "which is not one of ";

        var start = refusal.IndexOf(marker, StringComparison.Ordinal);

        if (start < 0) {
            return [];
        }

        var list = refusal[(start + marker.Length)..];
        var stop = list.IndexOf('.', StringComparison.Ordinal);

        if (stop >= 0) {
            list = list[..stop];
        }

        return [.. list.Split(", ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
    }

    /// <summary>Whether a refusal is about one named setting.</summary>
    static bool Names(string message, string setting) =>
        message.StartsWith($"'{setting}' is '", StringComparison.Ordinal);

    /// <summary>A graph of one node, with one of its settings written.</summary>
    /// <remarks>
    ///     ⚠ <b>Nothing is connected to it and nothing reads it.</b> The compiler walks every node
    ///     in the graph whatever the edges are, and a node whose inputs are missing reports about
    ///     those too — which is why <see cref="Names" /> matches the setting rather than the id
    ///     alone.
    /// </remarks>
    static NodeGraphModel One(string path, string setting, string value) {
        NodeGraphModel graph = new();

        graph.Add(path).SetText(setting, value);

        return graph;
    }

    static TextureGraphCompiler Compiler() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        return new(registry) { BaseWidth = 64, BaseHeight = 64, Seed = 41823 };
    }
}
