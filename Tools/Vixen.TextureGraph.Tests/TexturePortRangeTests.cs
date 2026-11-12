// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>What a port's declared range claims, held to the kernels it is a claim about.</summary>
/// <remarks>
///     ⚠ <b>A hard bound says a number past it means nothing, and that is checkable.</b> So the device
///     half evaluates every bounded port at its bound and past it: the two have to be one picture, or
///     the compile has to refuse. A range copied from a summary and wrong about the kernel fails here.
/// </remarks>
public class TexturePortRangeTests(ITestOutputHelper output) {
    const int Side = 32;

    static NodeTypeRegistry Registry() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        return registry;
    }

    static IEnumerable<PortDefinition> Numbers(NodeTypeDefinition type) =>
        type.Ports.Where(static port => port is { Direction: PortDirection.Input, Kind: PortKind.Float or PortKind.Int }
        );

    [Fact]
    public void Every_declared_range_holds_its_default_in_order() {
        var registry = Registry();

        foreach (var type in registry.Types) {
            foreach (var port in Numbers(type)) {
                var what = $"{type.Path}.{port.Name}";
                var value = port.Default.IsDefaultOrEmpty ? 0f : port.Default[0];

                Assert.True(port.Minimum <= port.Maximum, $"{what} has its bounds the wrong way round.");
                Assert.InRange(value, port.Minimum, port.Maximum);

                if (!float.IsInfinity(port.SoftMinimum)) {
                    Assert.True(
                        port.SoftMinimum >= port.Minimum && port.SoftMinimum <= value,
                        $"{what}'s soft minimum."
                    );
                }

                if (!float.IsInfinity(port.SoftMaximum)) {
                    Assert.True(
                        port.SoftMaximum <= port.Maximum && port.SoftMaximum >= value,
                        $"{what}'s soft maximum."
                    );
                }
            }
        }
    }

    /// <summary>The instrument: a table nobody filled in would satisfy every other test here.</summary>
    [Fact]
    public void Most_of_the_numbers_in_the_library_declare_a_range() {
        var ports = Registry().Types.SelectMany(Numbers).ToArray();
        var bounded = ports.Count(static port => !float.IsInfinity(port.Minimum)
            || !float.IsInfinity(port.Maximum)
            || !float.IsInfinity(port.SoftMinimum)
        );

        output.WriteLine($"{bounded} of {ports.Length} numeric ports declare a range.");

        Assert.True(bounded > ports.Length / 2, $"Only {bounded} of {ports.Length}.");
    }

    /// <summary>A published input takes the range of the port it feeds, unless two ports disagree.</summary>
    [Fact]
    public void A_compound_input_says_what_the_port_it_feeds_says() {
        var registry = Registry();
        var library = new TextureGraphLibrary();
        NodeGraphModel inner = new();
        var entry = inner.Add(SubGraphs.InputType);
        var hsl = inner.Add("Colour/HSL");
        var blur = inner.Add("Filters/Blur");

        inner.Interface.Add(new("Light", PortDirection.Input, PortKind.Float));
        inner.Interface.Add(new("Both", PortDirection.Input, PortKind.Float));
        inner.Connect(new(entry.Id, "Light"), new(hsl.Id, "Lightness"));
        inner.Connect(new(entry.Id, "Both"), new(hsl.Id, "Saturation"));
        inner.Connect(new(entry.Id, "Both"), new(blur.Id, "Radius"));
        inner.Connect(new(entry.Id, "Both"), new(hsl.Id, "Hue"));

        library.Publish("Compound/Ranged", inner, [], registry);

        var type = registry.Get("Compound/Ranged");
        var light = type.Port("Light", PortDirection.Input)!;
        var both = type.Port("Both", PortDirection.Input)!;

        Assert.Equal((-1f, 1f), (light.Minimum, light.Maximum));
        Assert.True(float.IsNegativeInfinity(both.Minimum) && float.IsNegativeInfinity(both.SoftMinimum));
    }

    /// <summary>⚠ The ceilings a plan enforces and the ranges a slider shows are one statement.</summary>
    [Theory]
    [InlineData("Filters/Blur HQ", "Sigma", TextureFilters.BlurHq, "sigma")]
    [InlineData("Filters/Directional Blur", "Length", TextureFilters.DirectionalBlur, "length")]
    [InlineData("Filters/Sharpen", "Radius", TextureFilters.Sharpen, "radius")]
    public void A_ceiling_is_the_ports_maximum(string node, string port, string kernel, string parameter) {
        var declared = Registry().Get(node).Port(port, PortDirection.Input)!;

        Assert.Equal(TextureKernelCeilings.Ceilings[(kernel, parameter)], declared.Maximum);
    }

    /// <summary>Past a hard bound is the bound, or a refusal: one picture, whichever kernel it is.</summary>
    [Fact]
    public void A_number_past_a_hard_bound_draws_what_the_bound_draws() {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);

        var registry = Registry();
        var drawn = 0;
        List<string> wrong = [];

        foreach (var type in registry.Types.OrderBy(static type => type.Path, StringComparer.Ordinal)) {
            if (type.Path.Contains("Mesh", StringComparison.Ordinal) || type.Path == "Source/Bitmap") {
                continue;
            }

            foreach (var port in Numbers(type)) {
                foreach (var bound in new[] { port.Minimum, port.Maximum }) {
                    if (float.IsInfinity(bound)) {
                        continue;
                    }

                    var past = bound == port.Minimum
                        ? bound - 1f
                        : bound + (port.Kind == PortKind.Int ? 1f : Math.Max(1f, bound));
                    var at = Draw(device, evaluator, registry, type, port, bound);
                    var beyond = Draw(device, evaluator, registry, type, port, past);

                    if (beyond is null) {
                        continue;
                    }

                    drawn++;

                    if (at is null || !at.SequenceEqual(beyond)) {
                        wrong.Add($"{type.Path}.{port.Name}: {bound} and {past}");
                    }
                }
            }
        }

        output.WriteLine($"{TextureKernelHarness.Adapter(device)}: {drawn} bounds drawn.");

        Assert.True(drawn > 30, $"Only {drawn} bounds were drawn.");
        Assert.Empty(wrong);
    }

    /// <summary>One node over noise with one number set, or null when a compile refuses it.</summary>
    static byte[]? Draw(
        IGraphicsDevice device,
        TexturePlanEvaluator evaluator,
        NodeTypeRegistry registry,
        NodeTypeDefinition type,
        PortDefinition port,
        float value
    ) {
        NodeGraphModel graph = new();
        var node = graph.Add(type.Path);

        if (type.Path == "Filters/Pixel Processor") {
            node.SetText("Expression", "a");
        }

        foreach (var input in type.Ports) {
            if (input is { Direction: PortDirection.Input, Kind: PortKind.Image }) {
                graph.Connect(new(graph.Add("Source/Noise").Id, "Out"), new(node.Id, input.Name));
            }
        }

        node.SetValue(port.Name, value);

        var compiler = new TextureGraphCompiler(registry) {
            BaseWidth = Side, BaseHeight = Side, Seed = 7, PreviewEveryNode = true
        };
        var compilation = compiler.Compile(graph);

        if (compilation.HasErrors) {
            return null;
        }

        using var uploads = new TextureUploads(device);

        if (TextureGraphExternals.Upload(uploads, compilation.Value, compiler.Externals).Length > 0) {
            return null;
        }

        using var bake = evaluator.Evaluate(compilation.Value, uploads.Externals);

        return [..bake.ReadTexels(compilation.Value.Outputs[^1])];
    }
}
