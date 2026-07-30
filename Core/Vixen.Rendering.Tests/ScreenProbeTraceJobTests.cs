// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Text.Json;
using Vixen.Rendering.Lighting;
using Xunit;

namespace Tests;

/// <summary>
///     The C# mirror of the screen-probe trace's job struct, against the offsets Raven assigned it.
/// </summary>
/// <remarks>
///     The same triangle as <see cref="IrradianceFillJobTests" />, for the same reason: std430 puts
///     the <c>float3</c> at sixteen where sequential layout would put it at eight, and getting it
///     wrong reads every job after the first out of the middle of the one before it — an atlas where
///     one probe is traced from where it stands and the rest from nowhere.
/// </remarks>
public class ScreenProbeTraceJobTests {
    [Theory]
    [InlineData("jobs.atlasOrigin", nameof(ScreenProbeTraceJob.AtlasOrigin))]
    [InlineData("jobs.valid", nameof(ScreenProbeTraceJob.Valid))]
    [InlineData("jobs.origin", nameof(ScreenProbeTraceJob.Origin))]
    public void EveryMemberSitsWhereTheShaderPutIt(string member, string field) {
        Assert.Equal(Members()[member], (int)Marshal.OffsetOf<ScreenProbeTraceJob>(field));
    }

    [Fact]
    public void OneJobIsAsWideAsTheShaderSaysAStrideIs() {
        Assert.Equal(32, ScreenProbeTraceJob.Stride);
        Assert.Equal(ScreenProbeTraceJob.Stride, Members()["jobs"]);
    }

    /// <summary>Where the job members are — offsets for the leaves, the size for the struct.</summary>
    static Dictionary<string, int> Members() {
        var path = Directory
            .EnumerateFiles(
                Path.Combine(AppContext.BaseDirectory, "Shaders"),
                "ScreenProbeTrace.reflect.json",
                SearchOption.AllDirectories
            )
            .FirstOrDefault();

        Assert.True(path is not null, "ScreenProbeTrace.reflect.json is not beside the tests, so there is nothing to check against");

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

        Assert.Fail("ScreenProbeTrace declares no binding called 'jobs', so the dispatch has nothing to index");

        return [];
    }
}
