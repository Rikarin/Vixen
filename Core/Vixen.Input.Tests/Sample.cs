// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input.Tests.Assets;

using Xunit;

namespace Vixen.Input.Tests;

/// <summary>The one asset every test in this project reads.</summary>
/// <remarks>
///     <para>
///         Its text comes from <c>SampleInput.Source</c> — the constant the generator emitted from
///         <c>Assets/SampleInput.vxinput</c>. So the fixture is the file on disk, and there is no
///         second copy of the document in a string literal to drift away from it.
///     </para>
///     <para>
///         That the reference below compiles at all is the generator's first assertion: if the
///         generator had produced nothing, or produced a class under a different name or namespace,
///         this file would not build.
///     </para>
/// </remarks>
static class Sample {
    /// <summary>The document, as the generator embedded it.</summary>
    public static string Text => SampleInput.Source;

    /// <summary>Reads it, failing the test if it does not read cleanly.</summary>
    public static InputActionAssetData Read() {
        var result = InputActionAssetReader.Read(Text, "SampleInput");

        Assert.Empty(result.Diagnostics);
        return result.Asset!;
    }

    /// <summary>Loads it as a runtime asset, with every map enabled.</summary>
    public static InputActions Load() {
        var actions = InputActions.Load(Text, "SampleInput");
        actions.Enable();
        return actions;
    }
}
