// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Vixen.Rendering.Lighting;
using Xunit;

namespace Tests;

/// <summary>
///     The C# mirror of <c>IrradianceFill.rvn</c>'s job struct, against the offsets Raven assigned it.
/// </summary>
/// <remarks>
///     <para>
///         <b>A struct that agrees with a comment agrees with nothing.</b> Three <c>float3</c>-shaped
///         members are twelve bytes each and thirty-six in all if a runtime is left to lay them out;
///         std430 rounds each to sixteen and the struct to forty-eight. Getting it wrong reads every
///         job after the first out of the middle of the one before it, and the picture is a field
///         where one brick is lit and the rest are lit from nowhere — which looks exactly like a
///         filler that is not finding any light.
///     </para>
///     <para>
///         Read off the reflection rather than written down here, because the reflection is what the
///         compiler produced and a second copy of a number is a number that can disagree. The shader
///         and its reflection are checked against each other in the Raven tree; this is the third
///         side of that triangle.
///     </para>
/// </remarks>
public class IrradianceFillJobTests {
    [Theory]
    [InlineData("jobs.origin", nameof(IrradianceFillJob.Origin))]
    [InlineData("jobs.position", nameof(IrradianceFillJob.Position))]
    [InlineData("jobs.spacing", nameof(IrradianceFillJob.Spacing))]
    public void EveryMemberSitsWhereTheShaderPutIt(string member, string field) {
        var expected = Members()[member];
        var actual = (int)Marshal.OffsetOf<IrradianceFillJob>(field);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void OneJobIsAsWideAsTheShaderSaysAStrideIs() {
        Assert.Equal(48, Unsafe.SizeOf<IrradianceFillJob>());
        Assert.Equal(IrradianceFillJob.Stride, Unsafe.SizeOf<IrradianceFillJob>());

        // The struct's own declared size, which is what a buffer of N jobs is N of. The members alone
        // do not settle it: the last one ends at forty-four, and a struct that stopped there would
        // put every job after the first four bytes early.
        Assert.Equal(IrradianceFillJob.Stride, Members()["jobs"]!);
    }

    /// <summary>
    ///     Where the shader's job members are — offsets for the leaves, and the size for the struct.
    /// </summary>
    /// <remarks>
    ///     The whole-struct entry is keyed by the binding's own name and carries its <c>Size</c>
    ///     rather than an offset, because that is the one number the leaves cannot imply.
    /// </remarks>
    static Dictionary<string, int> Members() {
        var path = Directory
            .EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Shaders"), "IrradianceFill.reflect.json", SearchOption.AllDirectories)
            .FirstOrDefault();

        Assert.True(path is not null, "IrradianceFill.reflect.json is not beside the tests, so there is nothing to check against");

        using var document = JsonDocument.Parse(File.ReadAllText(path!));

        foreach (var set in document.RootElement.GetProperty("Sets").EnumerateArray()) {
            foreach (var binding in set.GetProperty("Bindings").EnumerateArray()) {
                if (binding.GetProperty("Name").GetString() != "jobs") {
                    continue;
                }

                Dictionary<string, int> members = new(StringComparer.Ordinal) {
                    ["jobs"] = binding.GetProperty("Size").GetInt32()
                };

                foreach (var element in binding.GetProperty("Members").EnumerateArray()) {
                    var name = element.GetProperty("Name").GetString()!;

                    if (name != "jobs") {
                        members[name] = element.GetProperty("Offset").GetInt32();
                    }
                }

                return members;
            }
        }

        Assert.Fail("IrradianceFill declares no binding called 'jobs', so the dispatch has nothing to index");

        return [];
    }
}
