// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Text.Json;
using Vixen.Rendering.Lighting;
using Xunit;

namespace Tests;

/// <summary>The C# mirror of the accumulation's surface struct, against Raven's offsets.</summary>
/// <remarks>
///     The same triangle as <see cref="ScreenProbeTraceJobTests" />: the flag rides in the padding
///     std430 leaves after a <c>float3</c>, and a struct that agrees with a comment agrees with
///     nothing.
/// </remarks>
public class ScreenProbeSurfaceJobTests {
    [Theory]
    [InlineData("surfaces.position", nameof(ScreenProbeSurfaceJob.Position))]
    [InlineData("surfaces.valid", nameof(ScreenProbeSurfaceJob.Valid))]
    [InlineData("surfaces.normal", nameof(ScreenProbeSurfaceJob.Normal))]
    public void EveryMemberSitsWhereTheShaderPutIt(string member, string field) {
        Assert.Equal(Members()[member], (int)Marshal.OffsetOf<ScreenProbeSurfaceJob>(field));
    }

    [Fact]
    public void OneEntryIsAsWideAsTheShaderSaysAStrideIs() {
        Assert.Equal(32, ScreenProbeSurfaceJob.Stride);
        Assert.Equal(ScreenProbeSurfaceJob.Stride, Members()["surfaces"]);
    }

    static Dictionary<string, int> Members() {
        var path = Directory
            .EnumerateFiles(
                Path.Combine(AppContext.BaseDirectory, "Shaders"),
                "ScreenProbeAccumulate.reflect.json",
                SearchOption.AllDirectories
            )
            .FirstOrDefault();

        Assert.True(path is not null, "ScreenProbeAccumulate.reflect.json is not beside the tests");

        using var document = JsonDocument.Parse(File.ReadAllText(path!));

        foreach (var set in document.RootElement.GetProperty("Sets").EnumerateArray()) {
            foreach (var binding in set.GetProperty("Bindings").EnumerateArray()) {
                if (binding.GetProperty("Name").GetString() != "surfaces") {
                    continue;
                }

                Dictionary<string, int> members = new(StringComparer.Ordinal) {
                    ["surfaces"] = binding.GetProperty("Size").GetInt32()
                };

                foreach (var element in binding.GetProperty("Members").EnumerateArray()) {
                    var name = element.GetProperty("Name").GetString()!;

                    if (name != "surfaces") {
                        members[name] = element.GetProperty("Offset").GetInt32();
                    }
                }

                return members;
            }
        }

        Assert.Fail("ScreenProbeAccumulate declares no binding called 'surfaces'");

        return [];
    }
}
