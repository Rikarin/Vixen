using System.CommandLine;
using Vixen.Raven.Cli;
using Xunit;

namespace Tests;

/// <summary>
/// Phase 5: the compiler as a command line. Each test gets its own scratch
/// directory so the files written are real files, as they are in anger.
/// </summary>
public class CliTests : IDisposable {
    readonly string directory = Path.Combine(
        Path.GetTempPath(), "raven-cli-" + Guid.NewGuid().ToString("n")[..8]);

    readonly StringWriter output = new();
    readonly StringWriter error = new();

    public CliTests() => Directory.CreateDirectory(directory);

    public void Dispose() {
        if (Directory.Exists(directory)) {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void The_documented_invocation_works_front_to_back() {
        // Exactly what the README says: raven compile --target glsl <input> <output>
        var exitCode = Invoke("compile", "--target", "glsl", Fixture("lambert.rvn"), At(""));

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(At("Lambert.vert.glsl")));
        Assert.True(File.Exists(At("Lambert.frag.glsl")));
        Assert.StartsWith("#version 450", File.ReadAllText(At("Lambert.vert.glsl")));
    }

    [Fact]
    public void Glsl_is_the_default_target() {
        Assert.Equal(0, Invoke("compile", Fixture("lambert.rvn"), At("")));
        Assert.True(File.Exists(At("Lambert.frag.glsl")));
    }

    [Fact]
    public void Nothing_is_said_on_success_unless_asked() {
        Invoke("compile", Fixture("lambert.rvn"), At(""));
        Assert.Equal("", output.ToString());

        var verbose = new StringWriter();
        RavenCommand.Create(verbose, new StringWriter())
            .Parse(["compile", Fixture("lambert.rvn"), At("verbose"), "--verbose"])
            .Invoke();

        Assert.Contains("Lambert.vert.glsl", verbose.ToString());
        Assert.Contains("Lambert.frag.glsl", verbose.ToString());
    }

    [Fact]
    public void A_single_stage_shader_can_be_written_to_a_named_file() {
        var input = Write("one.rvn", """
            package A

            shader One {
                [PixelShader]
                func Pixel(): float4 {
                    return float4(1, 1, 1, 1)
                }
            }

            """);

        Assert.Equal(0, Invoke("compile", input, At("one.frag.glsl")));
        Assert.Contains("#version 450", File.ReadAllText(At("one.frag.glsl")));
    }

    [Fact]
    public void A_named_file_cannot_hold_more_than_one_stage() {
        // Two stages need two files, and guessing a second name would be worse
        // than saying so.
        Assert.Equal(2, Invoke("compile", Fixture("lambert.rvn"), At("everything.glsl")));
        Assert.Contains("names a single file", error.ToString());
        Assert.False(File.Exists(At("everything.glsl")));
    }

    [Fact]
    public void The_output_directory_is_created_if_it_is_missing() {
        Assert.Equal(0, Invoke("compile", Fixture("lambert.rvn"), At("nested/deeper")));
        Assert.True(File.Exists(At("nested/deeper/Lambert.vert.glsl")));
    }

    [Fact]
    public void Emit_ir_writes_the_dump_alongside() {
        Assert.Equal(0, Invoke("compile", Fixture("lambert.rvn"), At(""), "--emit-ir"));

        var ir = File.ReadAllText(At("lambert.ir"));
        Assert.Contains("shader Lambert", ir);
    }

    [Fact]
    public void A_semantic_error_is_rendered_with_its_source_and_fails_the_run() {
        var input = Write("bad.rvn", """
            package A

            shader S {
                [PixelShader]
                func Pixel(): float4 {
                    return float4(missing, 0, 0, 1)
                }
            }

            """);

        Assert.Equal(1, Invoke("compile", input, At("")));

        var reported = error.ToString();
        Assert.Contains("bad.rvn(6,23): error RVN2010", reported);
        Assert.Contains("return float4(missing, 0, 0, 1)", reported);
        Assert.Contains("^^^^^^^", reported);
        Assert.Contains("compilation failed with 1 error", reported);

        // Nothing half-written.
        Assert.Empty(Directory.GetFiles(directory, "*.glsl"));
    }

    [Fact]
    public void A_syntax_error_stops_before_the_binder_can_pile_on() {
        var input = Write("syntax.rvn", "package A\n\nshader S {\n    func F(: float {\n}\n");

        Assert.Equal(1, Invoke("compile", input, At("")));

        var reported = error.ToString();
        Assert.Contains("RVN1001", reported);
        Assert.DoesNotContain("RVN2", reported);
    }

    [Fact]
    public void An_informational_diagnostic_is_reported_once_and_does_not_fail_the_run() {
        // The shader has one sampler; GLSL folds it away and says so — once,
        // however many stages come out of the shader.
        Assert.Equal(0, Invoke("compile", Fixture("lambert.rvn"), At("")));

        var reported = error.ToString();
        Assert.Contains("info RVN4003", reported);
        Assert.Equal(1, Occurrences(reported, "RVN4003"));
    }

    [Fact]
    public void A_binary_target_writes_bytes_and_can_write_its_listing_too() {
        Assert.Equal(0, Invoke("compile", "-t", "spirv", Fixture("lambert.rvn"), At(""), "--emit-listing"));

        var binary = File.ReadAllBytes(At("Lambert.frag.spv"));
        Assert.Equal(0x03, binary[0]);
        Assert.Equal(0x02, binary[1]);
        Assert.Equal(0x23, binary[2]);
        Assert.Equal(0x07, binary[3]);

        // The listing is a separate file, because the .spv itself is unreadable.
        Assert.StartsWith("; SPIR-V", File.ReadAllText(At("Lambert.frag.spvasm")));
    }

    [Fact]
    public void A_missing_input_is_a_usage_error_not_a_compilation_failure() {
        Assert.Equal(2, Invoke("compile", At("nothing.rvn"), At("")));
        Assert.Contains("input file not found", error.ToString());
    }

    [Fact]
    public void An_unknown_target_is_rejected_and_the_known_ones_are_listed() {
        var code = CompileDriver.Run(
            new CompileRequest { Inputs = [Fixture("lambert.rvn")], Output = At(""), Target = "hlsl" },
            output,
            error);

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("unknown target 'hlsl'", error.ToString());
        Assert.Contains("glsl", error.ToString());
    }

    [Fact]
    public void A_bad_command_line_is_a_usage_error() {
        // No arguments, a missing output, and a target that does not exist —
        // each is caught by the parser, so `raven` never starts compiling.
        Assert.NotEmpty(Parse("compile").Errors);
        Assert.NotEmpty(Parse("compile", Fixture("lambert.rvn")).Errors);
        Assert.NotEmpty(Parse("compile", "-t", "metal", Fixture("lambert.rvn"), At("")).Errors);

        Assert.Equal(2, Invoke("compile", "-t", "metal", Fixture("lambert.rvn"), At("")));
    }

    [Fact]
    public void A_shader_with_no_entry_point_generates_nothing_and_says_so() {
        var input = Write("empty.rvn", """
            package A

            shader S {
                var tint: float4
            }

            """);

        Assert.Equal(1, Invoke("compile", input, At("")));
        Assert.Contains("no entry points", error.ToString());
    }

    int Invoke(params string[] args) =>
        RavenCommand.Create(output, error).Parse(args) is { Errors.Count: 0 } parsed
            ? parsed.Invoke()
            : (int)ExitCode.UsageError;

    ParseResult Parse(params string[] args) => RavenCommand.Create(output, error).Parse(args);

    string At(string relative) => System.IO.Path.Combine(directory, relative);

    string Write(string name, string source) {
        var path = At(name);
        File.WriteAllText(path, source);
        return path;
    }

    static int Occurrences(string text, string value) {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) {
            count++;
        }

        return count;
    }

    // bin/Debug/net10.0 -> Tests project root -> Fixtures
    static string Fixture(string file) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", file);
}
