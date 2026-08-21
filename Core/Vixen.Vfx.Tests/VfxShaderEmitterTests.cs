// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Core.Mathematics;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.CodeGen;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Vixen.Vfx;
using Xunit;

namespace Tests;

/// <summary>
///     The GPU backend's front half: a compiled graph emitted as a Raven compute shader — doc 06
///     § VFX pipeline's dual target.
/// </summary>
/// <remarks>
///     <para>
///         Every test here runs the emitted source through the real compiler rather than matching it
///         against a string. A generated shader that reads well and does not compile is the only
///         failure mode this stage really has, and a golden-text assertion is exactly the kind of test
///         that cannot see it.
///     </para>
///     <para>
///         What these cannot check is that the two backends produce the same numbers, because that
///         needs a device to run the module on. What they can check is everything up to it: that the
///         translation is well typed, that it binds what it touches and nothing else, and that the two
///         backends make the same decisions about what an initializer and an update step are.
///     </para>
/// </remarks>
public class VfxShaderEmitterTests {
    /// <summary>A fountain that reaches most of the opcode set.</summary>
    static VfxCompiledGraph Fountain() =>
        VfxCompiledGraph.Compile(
            [VfxSpawner.AtRate(60f)],
            [
                new(VfxOpcode.PositionInSphere, new Vector4(0f, 0f, 0f, 0.2f)),
                new(VfxOpcode.VelocityRandomDirection, new Vector4(2f, 4f, 0f, 0f)),
                new(VfxOpcode.SetLifetime, new Vector4(1f, 2f, 0f, 0f)),
                new(VfxOpcode.SetSize, new Vector4(0.1f, 0.3f, 0f, 0f)),
                new(VfxOpcode.SetColour, Vector4.One)
            ],
            [
                new(VfxOpcode.Gravity, new Vector4(0f, -9.81f, 0f, 0f)),
                new(VfxOpcode.Drag, new Vector4(0.5f, 0f, 0f, 0f)),
                new(VfxOpcode.Integrate),
                new(VfxOpcode.SizeOverLife, new Vector4(0.3f, 0f, 0f, 0f)),
                new(VfxOpcode.ColourOverLife, Vector4.One) { B = new(1f, 0f, 0f, 0f) }
            ],
            4096
        );

    /// <summary>Parses, binds, lowers and verifies. Returns everything that objected.</summary>
    static IReadOnlyList<Diagnostic> Check(string source) {
        var tree = SyntaxTree.ParseText(source, path: "Effect.rvn");

        if (tree.Diagnostics.Count > 0) {
            return tree.Diagnostics;
        }

        var compilation = Compilation.Create("Vfx", tree);
        var semantic = compilation.GetDiagnostics();

        if (semantic.Count > 0) {
            return semantic;
        }

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        IrVerifier.Verify(module, bag);

        return bag.ToArray();
    }

    /// <summary>Runs a backend over the source and asserts the whole pipeline was clean.</summary>
    static IReadOnlyList<GeneratedSource> Generate(string source, string target) {
        Assert.Empty(Check(source));

        var tree = SyntaxTree.ParseText(source, path: "Effect.rvn");
        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(Compilation.Create("Vfx", tree), bag);
        var backend = TargetBackends.Create(target);

        Assert.NotNull(backend);

        var generated = backend.Generate(module, bag);

        Assert.True(bag.IsEmpty, string.Join("\n", bag.Select(d => d.ToString())));

        return generated;
    }

    /// <summary>
    ///     The reference tool for a target, or a skip that names it and how to install it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Hoisted out of the callers' <see cref="Assert.All{T}(IEnumerable{T},Action{T})" />
    ///     deliberately.</b> <c>Assert.All</c> collects whatever its body throws and reports the
    ///     batch as one failure, so a skip raised inside the lambda would arrive as a red test
    ///     rather than a skipped one. The precondition is asked once, before the loop.
    ///     ⚠ It used to be neither: <c>Validate</c> returned early when the tool was missing, which
    ///     xUnit records as a pass. <c>Every_opcode_survives_both_reference_tools</c> asserts
    ///     nothing else at all, so on a machine with neither tool it was green having run neither
    ///     front end — the same defect as Raven's differential oracle, task #313.
    /// </remarks>
    static string RequireTool(string target) {
        var spirv = target == "spirv";
        var tool = spirv ? "spirv-val" : "glslangValidator";
        var executable = FindTool(tool);

        Assert.SkipUnless(
            executable is not null,
            $"{tool} is not on PATH (brew install {(spirv ? "spirv-tools" : "glslang")}, apt-get "
            + $"install {(spirv ? "spirv-tools" : "glslang-tools")}), so the generated shaders were "
            + "not put through a reference front end."
        );

        return executable!;
    }

    /// <summary>
    ///     Hands a generated unit to the reference tool for its target.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Neither is a build dependency, so an absent one is skipped rather than failed. When one
    ///         is there it is worth every millisecond: a generated shader that reads perfectly can
    ///         still be a module no driver would load, and both of these have caught exactly that.
    ///     </para>
    ///     <para>
    ///         Both targets, not one, because they disagree about what they will accept. A store into
    ///         a buffer decorated read-only is a compile error in GLSL and a module <c>spirv-val</c>
    ///         waves through — which is how a lowering bug in the shared path came to look like a
    ///         backend bug in one of them.
    ///     </para>
    /// </remarks>
    static void Validate(GeneratedSource unit, string target, string executable) {
        var spirv = target == "spirv";
        var tool = spirv ? "spirv-val" : "glslangValidator";

        var path = Path.Combine(Path.GetTempPath(), $"vfx_{Guid.NewGuid():n}{(spirv ? ".spv" : ".comp")}");

        if (unit.Binary is { } binary) {
            File.WriteAllBytes(path, binary);
        } else {
            File.WriteAllText(path, unit.Code);
        }

        string[] arguments = spirv
            ? ["--target-env", "vulkan1.0", path]
            : ["-V", path, "-o", Path.ChangeExtension(path, ".spv")];

        try {
            var process = Process.Start(
                new ProcessStartInfo(executable, arguments) {
                    RedirectStandardOutput = true, RedirectStandardError = true
                }
            )!;

            // Drained before the wait rather than after it: glslangValidator on a shader it dislikes
            // writes more than a pipe holds, and a parent waiting on the exit while the child waits
            // on the write is a hang with nothing in the log. The same shape as GoldenSpirvTests,
            // which is where it actually happened.
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();

            process.WaitForExit();

            var log = output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult();

            Assert.True(process.ExitCode == 0, $"{tool} rejected {unit.Name}:\n{log}\n\n{unit.Code}");
        } finally {
            File.Delete(path);
        }
    }

    // ⚠ The name is tried with Windows' suffix as well as without. A tool on PATH as spirv-val.exe
    // is not a file called spirv-val, and a lookup that only asks for the bare name reports "not
    // installed" for a validator sitting right there — which reads as a skip and is really a hole.
    static string? FindTool(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator)
        .Concat(["/opt/homebrew/bin", "/usr/local/bin"])
        .Where(directory => !string.IsNullOrWhiteSpace(directory))
        .SelectMany(directory => OperatingSystem.IsWindows()
            ? new[] { Path.Combine(directory, name + ".exe"), Path.Combine(directory, name) }
            : [Path.Combine(directory, name)])
        .FirstOrDefault(File.Exists);

    static void Clean(string source) {
        var diagnostics = Check(source);

        Assert.True(diagnostics.Count == 0, source + "\n\n" + string.Join("\n", diagnostics.Select(d => d.ToString())));
    }

    // --- It compiles -------------------------------------------------------

    [Fact]
    public void A_fountain_compiles() {
        Clean(VfxShaderEmitter.Emit(Fountain(), "Fountain").Source);
    }

    /// <summary>
    ///     Every opcode, in one graph, so a new one cannot be added without something compiling it.
    /// </summary>
    [Fact]
    public void Every_opcode_compiles() {
        Clean(VfxShaderEmitter.Emit(Everything(), "Everything").Source);
    }

    /// <summary>A graph reaching every opcode there is.</summary>
    static VfxCompiledGraph Everything() =>
        VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(64)],
            [
                new(VfxOpcode.SetPosition, new Vector4(1f, 2f, 3f, 0f)),
                new(VfxOpcode.PositionInSphere, new Vector4(0f, 1f, 0f, 2f)),
                new(VfxOpcode.PositionInBox, new Vector4(-1f, -1f, -1f, 0f)) { B = new(1f, 1f, 1f, 0f) },
                new(VfxOpcode.SetVelocity, new Vector4(0f, 5f, 0f, 0f)),
                new(VfxOpcode.VelocityRandomDirection, new Vector4(1f, 3f, 0f, 0f)),
                new(VfxOpcode.VelocityInCone, new Vector4(0f, 1f, 0f, 0.4f)) { B = new(2f, 6f, 0f, 0f) },
                new(VfxOpcode.SetLifetime, new Vector4(1f, 4f, 0f, 0f)),
                new(VfxOpcode.SetSize, new Vector4(0.1f, 0.5f, 0f, 0f)),
                new(VfxOpcode.SetColour, new Vector4(0.2f, 0.4f, 0.8f, 1f)),
                new(VfxOpcode.SetRotation, new Vector4(0f, 6.28f, 0f, 0f)),
                new(VfxOpcode.SetAngularVelocity, new Vector4(-2f, 2f, 0f, 0f)),
                new(VfxOpcode.SetCustom, new Vector4(1.5f, 0f, 0f, 0f)),
                new(VfxOpcode.RandomCustom, new Vector4(0f, 0f, 0f, 0f)) { B = new(1f, 1f, 1f, 0f), Slot = 1 },
                new(VfxOpcode.SetCustom, new Vector4(0.1f, 0.2f, 0.3f, 0.4f)) { Slot = 2 }
            ],
            [
                new(VfxOpcode.Gravity, new Vector4(0f, -9.81f, 0f, 0f)),
                new(VfxOpcode.Drag, new Vector4(0.75f, 0f, 0f, 0f)),
                new(VfxOpcode.Attract, new Vector4(0f, 3f, 0f, 8f)) { B = new(5f, 0f, 0f, 0f) },
                new(VfxOpcode.Attract, new Vector4(2f, 0f, 0f, -4f)),
                new(VfxOpcode.Vortex, new Vector4(0f, 0f, 0f, 6f)) { B = new(0f, 1f, 0f, 4f) },
                new(VfxOpcode.Turbulence, new Vector4(0.4f, 0.4f, 0.4f, 3f)) { B = new(0.5f, 3f, 0f, 0f) },
                new(VfxOpcode.Integrate),
                new(VfxOpcode.CollidePlane, new Vector4(0f, 1f, 0f, -2f)) { B = new(0.6f, 0.2f, 0f, 0f) },
                new(VfxOpcode.CollideSphere, new Vector4(0f, 0f, 0f, 1.5f)) { B = new(0.8f, 0.1f, 0f, 0f) },
                new(VfxOpcode.Rotate),
                new(VfxOpcode.SizeOverLife, new Vector4(0.5f, 0f, 0f, 0f)),
                new(VfxOpcode.ColourOverLife, Vector4.One) { B = Vector4.Zero },
                new(VfxOpcode.CustomOverLife, new Vector4(1f, 0f, 0f, 0f)) { B = Vector4.Zero },
                new(VfxOpcode.CustomOverLife, new Vector4(0f, 0f, 0f, 0f)) { B = new(1f, 1f, 1f, 0f), Slot = 1 }
            ],
            1024,
            customs: [
                new("mass", VfxAttributeType.Float),
                new("drift", VfxAttributeType.Float3),
                new("stain", VfxAttributeType.Float4)
            ]
        );

    /// <summary>
    ///     A graph with nothing but a position still compiles: no lifetime means no age, which means
    ///     no update kernel at all rather than an empty one.
    /// </summary>
    [Fact]
    public void A_graph_with_no_updaters_emits_no_update_kernel() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(1)],
            [new(VfxOpcode.SetPosition, new Vector4(0f, 1f, 0f, 0f))],
            [],
            16
        );

        var shader = VfxShaderEmitter.Emit(graph, "Static");

        Assert.True(shader.HasInitialize);
        Assert.False(shader.HasUpdate);
        Assert.DoesNotContain("StaticUpdate", shader.Source, StringComparison.Ordinal);

        Clean(shader.Source);
    }

    // --- What it binds -----------------------------------------------------

    [Fact]
    public void It_binds_what_the_kernels_touch_and_nothing_else() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.AtRate(10f)],
            [new(VfxOpcode.PositionInBox, new Vector4(-1f, 0f, -1f, 0f)) { B = new(1f, 0f, 1f, 0f) }],
            [],
            64
        );

        var shader = VfxShaderEmitter.Emit(graph, "Box");

        // The graph stores colour and size because its renderer would read them; these kernels never
        // touch either, and a descriptor bound for nothing is still a descriptor.
        Assert.Equal(
            [VfxAttribute.Position, VfxAttribute.Identifier],
            shader.Bindings.Select(binding => binding.Attribute)
        );
    }

    /// <summary>The identifier is read and never written, which is one access decoration.</summary>
    [Fact]
    public void The_identifier_is_read_only() {
        var shader = VfxShaderEmitter.Emit(Fountain(), "Fountain");

        var particles = shader.Bindings.Where(binding => binding.Role == VfxBindingRole.Particle).ToArray();
        var identifier = Assert.Single(particles, binding => binding.Attribute == VfxAttribute.Identifier);

        Assert.False(identifier.IsWritten);
        Assert.Contains("var identifier: Buffer<uint>", shader.Source, StringComparison.Ordinal);

        Assert.All(
            particles.Where(binding => binding.Attribute != VfxAttribute.Identifier),
            binding => Assert.True(binding.IsWritten)
        );

        // ⚠ And its compacted twin *is* written, which is the one place the read-only rule inverts.
        // A survivor carries its identifier to its new slot or its randomness is re-rolled the first
        // time anything ahead of it dies — see `VfxShaderEmitter.Compacted`.
        Assert.True(
            Assert.Single(
                shader.Bindings,
                binding => binding.Role == VfxBindingRole.Compacted
                    && binding.Attribute == VfxAttribute.Identifier
            ).IsWritten
        );
    }

    /// <summary>
    ///     A three-component attribute is sixteen bytes, not twelve — std430 rounds a vec3 array's
    ///     stride up whatever it is declared as, and a host uploading packed <c>Vector3</c>s would
    ///     read every particle after the first from the wrong offset.
    /// </summary>
    [Fact]
    public void A_position_occupies_sixteen_bytes() {
        var shader = VfxShaderEmitter.Emit(Fountain(), "Fountain");

        var particles = shader.Bindings.Where(binding => binding.Role == VfxBindingRole.Particle).ToArray();

        Assert.Equal(16, Assert.Single(particles, b => b.Attribute == VfxAttribute.Position).Stride);
        Assert.Equal(4, Assert.Single(particles, b => b.Attribute == VfxAttribute.Size).Stride);
        Assert.Contains("var position: RWBuffer<float4>", shader.Source, StringComparison.Ordinal);

        // The compacted twin has the same stride by construction, and a host that sized it any other
        // way would write a survivor's position into the middle of the one before it.
        Assert.All(
            shader.Bindings.Where(binding => binding.Role == VfxBindingRole.Compacted),
            binding => Assert.Equal(
                Assert.Single(particles, particle => particle.Name + "Out" == binding.Name).Stride,
                binding.Stride
            )
        );
    }

    // --- Where the two backends have to agree ------------------------------

    /// <summary>
    ///     An initializer in the updater list is skipped by both backends, so it reaches neither the
    ///     update kernel nor the bindings.
    /// </summary>
    /// <remarks>
    ///     The CPU's update sweep drops these in <c>VfxSimulation.Apply</c>'s default case. If the
    ///     emitter kept them, an effect would reset its colour every frame on the GPU and not on the
    ///     CPU — a difference no amount of staring at the source would explain.
    /// </remarks>
    [Fact]
    public void An_initializer_in_the_updater_list_reaches_no_kernel() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(4)],
            [new(VfxOpcode.SetPosition, Vector4.Zero), new(VfxOpcode.SetVelocity, new Vector4(0f, 1f, 0f, 0f))],
            [new(VfxOpcode.SetColour, Vector4.One), new(VfxOpcode.Integrate)],
            32
        );

        var shader = VfxShaderEmitter.Emit(graph, "Skipped");

        Assert.DoesNotContain("colour", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain(shader.Bindings, binding => binding.Attribute == VfxAttribute.Colour);

        Clean(shader.Source);
    }

    /// <summary>
    ///     An updater in the initializer list runs with a step of zero, which is what the CPU backend
    ///     passes it.
    /// </summary>
    [Fact]
    public void An_updater_in_the_initializer_list_runs_with_no_step() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(4)],
            [
                new(VfxOpcode.SetPosition, Vector4.Zero),
                new(VfxOpcode.SetVelocity, new Vector4(0f, 1f, 0f, 0f)),
                new(VfxOpcode.Integrate)
            ],
            [],
            32
        );

        var shader = VfxShaderEmitter.Emit(graph, "AtBirth");

        Assert.Contains("velocity[slot].xyz * 0f", shader.Source, StringComparison.Ordinal);

        Clean(shader.Source);
    }

    /// <summary>
    ///     Age is the runtime's own: nothing in the graph writes it, and both kernels do.
    /// </summary>
    [Fact]
    public void Age_is_zeroed_at_birth_and_advanced_every_step() {
        var shader = VfxShaderEmitter.Emit(Fountain(), "Fountain");

        Assert.Contains("age[slot] = 0f", shader.Source, StringComparison.Ordinal);
        Assert.Contains("age[slot] = age[slot] + deltaTime", shader.Source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The salts the shader draws on are the ones the graph assigned, not ones counted again here.
    /// </summary>
    /// <remarks>
    ///     Counting them a second time is the obvious way to write this and would agree with the CPU
    ///     until the first graph that arrived with a salt already set — which is exactly the graph a
    ///     golden test pins.
    /// </remarks>
    [Fact]
    public void The_salts_come_from_the_graph() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(4)],
            [new(VfxOpcode.SetLifetime, new Vector4(1f, 2f, 0f, 0f), salt: 4242)],
            [],
            32
        );

        Assert.Contains("Range(particle, 4242u,", VfxShaderEmitter.Emit(graph, "Pinned").Source, StringComparison.Ordinal);
    }

    /// <summary>A parameter is written with every digit it has, or the GPU runs a different effect.</summary>
    [Fact]
    public void A_parameter_round_trips_through_the_source() {
        const float Awkward = 0.1234567f;

        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(1)],
            [new(VfxOpcode.SetSize, new Vector4(Awkward, Awkward, 0f, 0f))],
            [],
            8
        );

        var source = VfxShaderEmitter.Emit(graph, "Precise").Source;

        Assert.Contains(Awkward.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "f", source, StringComparison.Ordinal);

        Clean(source);
    }

    /// <summary>A value small enough to need an exponent is still a literal the lexer takes.</summary>
    [Fact]
    public void A_tiny_parameter_is_still_a_literal() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(1)],
            [new(VfxOpcode.SetSize, new Vector4(1e-9f, 2e-9f, 0f, 0f))],
            [],
            8
        );

        Clean(VfxShaderEmitter.Emit(graph, "Tiny").Source);
    }

    /// <summary>
    ///     Two fields of one kind in one graph declare two sets of locals, not one twice.
    /// </summary>
    /// <remarks>
    ///     A field needs a distance before it can use one, and a distance needs a name — so the name
    ///     carries the operation's position. Without it the second attractor redeclares
    ///     <c>distance</c> in the same scope, which the compiler catches; the reason it is a test is
    ///     that the first draft numbered them from a counter that was never reset between calls to
    ///     <c>Emit</c>, and that produces a shader that compiles and belongs to the wrong graph.
    /// </remarks>
    [Fact]
    public void Two_fields_of_a_kind_do_not_collide() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(4)],
            [new(VfxOpcode.SetPosition, Vector4.Zero), new(VfxOpcode.SetVelocity, Vector4.Zero)],
            [
                new(VfxOpcode.Attract, new Vector4(0f, 0f, 0f, 1f)),
                new(VfxOpcode.Attract, new Vector4(1f, 0f, 0f, 1f))
            ],
            32
        );

        var shader = VfxShaderEmitter.Emit(graph, "Twin");

        Assert.Contains("distance0", shader.Source, StringComparison.Ordinal);
        Assert.Contains("distance1", shader.Source, StringComparison.Ordinal);

        Clean(shader.Source);

        // And emitting the same graph again gives the same source. A counter carried between calls
        // would number the second one's locals from where the first left off.
        Assert.Equal(shader.Source, VfxShaderEmitter.Emit(graph, "Twin").Source);
    }

    /// <summary>
    ///     The noise field's step is the one constant both sides must agree on, so it is emitted from
    ///     the shared constant rather than written out again.
    /// </summary>
    /// <remarks>
    ///     A curl is a derivative, and two derivatives taken over different steps are two different
    ///     fields — close enough to look right in a screenshot and far enough apart to fail the
    ///     agreement test the whole dual target exists for.
    /// </remarks>
    [Fact]
    public void Curl_noise_takes_its_derivative_over_the_shared_step() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(4)],
            [new(VfxOpcode.SetPosition, Vector4.Zero), new(VfxOpcode.SetVelocity, Vector4.Zero)],
            [new(VfxOpcode.Turbulence, new Vector4(1f, 1f, 1f, 2f)) { B = new(1f, 2f, 0f, 0f) }],
            32
        );

        var shader = VfxShaderEmitter.Emit(graph, "Swirl");
        var epsilon = VfxNoise.Epsilon.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Contains($"float3({epsilon}f, 0f, 0f)", shader.Source, StringComparison.Ordinal);
        Assert.Contains($"/ {(2f * VfxNoise.Epsilon).ToString("R", System.Globalization.CultureInfo.InvariantCulture)}f", shader.Source, StringComparison.Ordinal);

        // Two octaves' worth of loop, and the graph's salt rather than a counted one.
        Assert.Contains($"{graph.Updaters[0].Salt}u, 2)", shader.Source, StringComparison.Ordinal);

        Clean(shader.Source);
    }

    /// <summary>
    ///     A graph with turbulence and nothing random still gets the hash, which the noise needs.
    /// </summary>
    /// <remarks>
    ///     The hash used to be emitted from inside the random-draw helpers, so a graph whose only
    ///     user of it was a noise field emitted a call to a function that was not there. It compiled
    ///     nowhere, which is the good version of this mistake.
    /// </remarks>
    [Fact]
    public void A_noise_field_gets_the_hash_without_anything_random() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(4)],
            [new(VfxOpcode.SetPosition, Vector4.Zero), new(VfxOpcode.SetVelocity, Vector4.Zero)],
            [new(VfxOpcode.Turbulence, new Vector4(1f, 1f, 1f, 2f)) { B = new(0f, 1f, 0f, 0f) }],
            32
        );

        var shader = VfxShaderEmitter.Emit(graph, "Wind");

        Assert.Contains("func Hash(value: uint): uint", shader.Source, StringComparison.Ordinal);

        // And not the per-particle draws, which nothing here uses — nor the identifier they read.
        Assert.DoesNotContain("func Value(", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain(shader.Bindings, binding => binding.Attribute == VfxAttribute.Identifier);

        Clean(shader.Source);
    }

    // --- What it refuses ---------------------------------------------------

    [Fact]
    public void A_cone_about_a_zero_axis_is_refused() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(1)],
            [new(VfxOpcode.VelocityInCone, new Vector4(0f, 0f, 0f, 0.5f)) { B = new(1f, 2f, 0f, 0f) }],
            [],
            8
        );

        Assert.Throws<ArgumentException>(() => VfxShaderEmitter.Emit(graph, "Degenerate"));
    }

    [Fact]
    public void A_name_that_is_not_an_identifier_is_refused() {
        Assert.Throws<ArgumentException>(() => VfxShaderEmitter.Emit(Fountain(), "My Effect"));
    }

    // --- What comes out the far end ----------------------------------------

    /// <summary>All three kernels reach the backends, as compute units a reference tool accepts.</summary>
    [Fact]
    public void Every_kernel_generates_glsl_a_front_end_accepts() {
        var shader = VfxShaderEmitter.Emit(Fountain(), "Fountain");
        var generated = Generate(shader.Source, "glsl");

        Assert.True(shader.HasReap);
        Assert.Equal(3, generated.Count);
        Assert.All(generated, unit => Assert.Equal(ShaderStage.Compute, unit.Stage));

        // The names the shader promises are the names that came out. They are spelled in two places
        // — the emitter and the accessors a host dispatches through — and nothing else would notice
        // them drifting apart.
        Assert.Single(generated, unit => unit.Name.StartsWith(shader.InitializeShader, StringComparison.Ordinal));
        Assert.Single(generated, unit => unit.Name.StartsWith(shader.UpdateShader, StringComparison.Ordinal));
        Assert.Single(generated, unit => unit.Name.StartsWith(shader.ReapShader, StringComparison.Ordinal));

        // ⚠ Last, and that ordering is load-bearing. A skip ends the method, so everything this test
        // can check without a front end is checked before the front end is asked for — otherwise
        // gating the tool would quietly take the shape and naming assertions down with it on every
        // machine that has no glslang.
        var glslang = RequireTool("glsl");

        Assert.All(generated, unit => Validate(unit, "glsl", glslang));
    }

    /// <summary>And SPIR-V, which is the one a device would be handed.</summary>
    [Fact]
    public void The_kernels_reach_spirv_a_validator_accepts() {
        var generated = Generate(VfxShaderEmitter.Emit(Fountain(), "Fountain").Source, "spirv");

        Assert.Equal(3, generated.Count);
        Assert.All(generated, unit => Assert.NotNull(unit.Binary));

        var validator = RequireTool("spirv");

        Assert.All(generated, unit => Validate(unit, "spirv", validator));
    }

    /// <summary>
    ///     A graph with no lifetime gets no reap kernel, and therefore none of its storage.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The buffers are the reason this is a test rather than a detail.</b> The compacted set
    ///     is a second copy of every attribute — it doubles what an effect costs on the device — and
    ///     an effect whose particles never die has nothing for a compaction to do. Emitting one
    ///     anyway would be a bill nobody could see they were paying.
    /// </remarks>
    [Fact]
    public void A_graph_with_no_lifetime_has_no_reap_kernel_and_no_second_set_of_buffers() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(4)],
            [new(VfxOpcode.SetVelocity, new Vector4(0f, 1f, 0f, 0f))],
            [new(VfxOpcode.Integrate)],
            8
        );

        var shader = VfxShaderEmitter.Emit(graph, "Endless");

        Assert.False(shader.HasReap);
        Assert.All(shader.Bindings, binding => Assert.Equal(VfxBindingRole.Particle, binding.Role));
        Assert.DoesNotContain("survivors", shader.Source, StringComparison.Ordinal);
        Assert.Equal(2, Generate(shader.Source, "spirv").Count);
    }

    /// <summary>The survivors claim their slots with an atomic, which is what makes it one pass.</summary>
    [Fact]
    public void The_reap_kernel_claims_a_slot_per_survivor() {
        var shader = VfxShaderEmitter.Emit(Fountain(), "Fountain");

        Assert.Contains("shader FountainReap : FountainCommon", shader.Source, StringComparison.Ordinal);
        Assert.Contains("if (age[slot] >= lifetime[slot])", shader.Source, StringComparison.Ordinal);
        Assert.Contains("atomicAdd(survivors[0], 1u)", shader.Source, StringComparison.Ordinal);

        // ⚠ Every attribute is copied, not only the written ones. What a survivor leaves behind it
        // does not get back.
        foreach (var binding in shader.Bindings.Where(binding => binding.Role == VfxBindingRole.Particle)) {
            Assert.Contains($"{binding.Name}Out[to] = {binding.Name}[slot]", shader.Source, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     And so does every opcode, which is where the reference tools earn their place: the
    ///     compiler's own diagnostics are a weaker statement than a front end that has to load it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>One test per front end, and not one test asking for both.</b> A skip is all-or-
    ///     nothing, so a single case gated on both tools would stand the SPIR-V half aside on every
    ///     machine that merely lacks glslang — which is two of the three CI legs, and is precisely
    ///     the coverage this sweep exists to stop losing quietly. Split, each half runs wherever its
    ///     own tool is.
    /// </remarks>
    [Fact]
    public void Every_opcode_survives_the_glsl_front_end() {
        var source = VfxShaderEmitter.Emit(Everything(), "Everything").Source;
        var generated = Generate(source, "glsl");
        var glslang = RequireTool("glsl");

        Assert.All(generated, unit => Validate(unit, "glsl", glslang));
    }

    /// <summary>And the same for SPIR-V, which is the one a device would be handed.</summary>
    [Fact]
    public void Every_opcode_survives_the_spirv_validator() {
        var source = VfxShaderEmitter.Emit(Everything(), "Everything").Source;
        var generated = Generate(source, "spirv");
        var validator = RequireTool("spirv");

        Assert.All(generated, unit => Validate(unit, "spirv", validator));
    }

    /// <summary>
    ///     <c>spirv-val</c> is installed, so the validating tests above mean something.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The skips those tests raise are honest but quiet, and a suite that skips its own
    ///         subject is a green build asserting nothing. This is the one place that says so out
    ///         loud, in the precedent of
    ///         <c>SpirvBackendTests.The_validator_is_installed_so_these_tests_mean_something</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>spirv-val</c> only, and not <c>glslangValidator</c>.</b> All three CI legs
    ///         install SPIR-V Tools — Linux and macOS by package, Windows with the Vulkan SDK — so
    ///         demanding it here is a claim the workflow already keeps. No leg installs glslang,
    ///         so the GLSL half stays a skip: a guard test with no install step behind it only
    ///         trades a false green for a false red. Adding <c>glslang-tools</c> (Linux) and
    ///         <c>glslang</c> (macOS) to the workflow is what would earn its own guard.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_validator_is_installed_so_these_tests_mean_something() =>
        Assert.True(
            FindTool("spirv-val") is not null,
            "spirv-val was not found. Install SPIR-V Tools (brew install spirv-tools, apt-get "
            + "install spirv-tools) — without it the emitter tests check the listing, not whether "
            + "any front end would load the module."
        );
}
