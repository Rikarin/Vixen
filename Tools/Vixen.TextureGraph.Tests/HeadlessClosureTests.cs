// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.TextureGraph.Tests;

/// <summary>This assembly exists so that evaluating a texture graph links no interface.</summary>
/// <remarks>
///     ⚠ <b>The check reads the output folder, not the project references.</b>
///     <c>DisableTransitiveProjectReferences</c> narrows what a project may spell and leaves the
///     copy-local closure alone, which is how the evaluator came to load the whole interface
///     framework with every compile green (#720). Signals are allowed: <c>Vixen.Ui.Reactive</c>
///     references Core and nothing with a widget in it.
/// </remarks>
public sealed class HeadlessClosureTests {
    [Fact]
    public void No_interface_assembly_reaches_the_output_folder() {
        var assemblies = Directory.GetFiles(AppContext.BaseDirectory, "Vixen.*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .ToArray();

        Assert.Contains("Vixen.TextureGraph", assemblies);
        Assert.DoesNotContain(
            assemblies,
            static name => name!.StartsWith("Vixen.Editor.", StringComparison.Ordinal)
                || (name.StartsWith("Vixen.Ui", StringComparison.Ordinal) && name != "Vixen.Ui.Reactive")
        );
    }
}
