// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     A graph's curve or ramp parameter (Patina #181): the text a <c>Curve</c> or <c>Ramp</c> setting holds, handed
///     whole into a setting written <c>$Name</c>, so a published graph names one curve and its user draws it.
/// </summary>
/// <remarks>
///     ⚠ <b>The assertions are on the baked table, not on the setting's text.</b> Before these kinds, a text parameter
///     with no list was a scalar and <c>$Shape</c> reached the node unchanged, which bakes nothing: the table compared
///     here would have been missing, not merely different.
/// </remarks>
public class TextureGraphTableParameterTests {
    const string Falling = "curve:r=0 1,1 0;g=0 1,1 0;b=0 1,1 0";
    const string Rising = "curve:r=0 0,1 1;g=0 0,1 1;b=0 0,1 1";
    const string Warm = "ramp:0 000000ff,1 ff8000ff";

    static TextureGraphParameter Shape(string text = Falling) =>
        new("Shape", TextureGraphParameterKind.Curve, Choice: text);

    static TextureGraphParameter Tint(string text = Warm) => new("Tint", TextureGraphParameterKind.Ramp, Choice: text);

    static (TextureGraphCompiler Compiler, NodeGraphModel Graph) Through(string type, string setting, string text) {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var table = graph.Add(type);
        var output = graph.Add("Output/Output");

        graph.Connect(new(noise.Id, "Out"), new(table.Id, "Input"));
        graph.Connect(new(table.Id, "Out"), new(output.Id, "Input"));
        table.SetText(setting, text);

        TextureGraphCompiler compiler = new(registry) { BaseWidth = 64, BaseHeight = 64, Seed = 3 };

        compiler.Parameters.Add(Shape());
        compiler.Parameters.Add(Tint());

        return (compiler, graph);
    }

    static byte[] TableOf(TextureGraphCompiler compiler) =>
        [..Assert.Single(compiler.Externals, static e => !e.Texels.IsDefaultOrEmpty).Texels];

    [Fact]
    public void A_curve_parameter_is_baked_where_a_setting_names_it() {
        var (compiler, graph) = Through("Colour/Curve", "Curve", "$Shape");

        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);
        Assert.Equal(TextureRamp.FromText(Falling), TableOf(compiler));
    }

    [Fact]
    public void A_ramp_parameter_is_baked_where_a_setting_names_it() {
        var (compiler, graph) = Through("Colour/Gradient Map", "Ramp", "$Tint");

        Assert.Empty(compiler.Compile(graph).Diagnostics);
        Assert.Equal(TextureRamp.FromText(Warm), TableOf(compiler));
    }

    [Fact]
    public void A_host_override_bakes_its_own_curve() {
        var (compiler, graph) = Through("Colour/Curve", "Curve", "$Shape");

        compiler.Arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["Shape"] = Rising };

        Assert.Empty(compiler.Compile(graph).Diagnostics);
        Assert.Equal(TextureRamp.FromText(Rising), TableOf(compiler));
    }

    [Theory]
    [InlineData("Gold")]
    [InlineData(Warm)]
    [InlineData("curve:r=nonsense")]
    public void An_override_that_is_not_a_curve_keeps_the_default_and_says_so(string written) {
        var (compiler, graph) = Through("Colour/Curve", "Curve", "$Shape");

        compiler.Arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["Shape"] = written };
        var compilation = compiler.Compile(graph);

        Assert.Contains(
            compilation.Diagnostics,
            static d => d.Message.Contains("not a curve written inline", StringComparison.Ordinal)
        );
        Assert.Equal(TextureRamp.FromText(Falling), TableOf(compiler));
    }

    [Theory]
    [InlineData("")]
    [InlineData(Falling)]
    public void A_curve_default_that_bakes_or_is_empty_is_accepted(string text) =>
        Assert.Empty(TextureGraphParameters.Check([Shape(text)]));

    [Theory]
    [InlineData("Gold")]
    [InlineData(Warm)]
    public void A_curve_default_that_does_not_bake_is_refused(string text) =>
        Assert.Single(TextureGraphParameters.Check([Shape(text)]));

    [Fact]
    public void A_curve_and_a_ramp_are_their_setting_kinds_and_back() {
        var settings = TextureGraphParameters.Settings([Shape(), Tint()]);

        Assert.Equal(
            [(SettingKind.Curve, Falling), (SettingKind.Ramp, Warm)],
            settings.Select(static s => (s.Kind, s.Default))
        );
        Assert.Equal(
            [Shape(), Tint()],
            TextureGraphParameters.Declared(settings).Select(static p => p with { Group = string.Empty })
        );
    }
}
