// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.App.Tests;

public class BuildVariantTests {
    [Fact]
    public void TheCommandLineWinsOverEverything() =>
        Assert.Equal(BuildVariant.Server, BuildVariants.Detect(BuildVariant.Server));

    /// <summary>
    ///     With nothing to go on the fallback is the compilation's own <c>DEBUG</c> flag, which is a
    ///     poor answer — it says nothing about whether content is bundled — but is better than
    ///     refusing to start.
    /// </summary>
    [Fact]
    public void WithNothingToGoOnItFallsBackToTheCompilation() {
        var detected = BuildVariants.Detect(null);

        Assert.True(detected is BuildVariant.Debug or BuildVariant.Release);
    }

    /// <summary>
    ///     The difference at boot: a server picks the headless platform and every subsystem takes the
    ///     path it already had to have.
    /// </summary>
    [Fact]
    public void OnlyTheServerVariantIsHeadless() {
        foreach (var variant in Enum.GetValues<BuildVariant>()) {
            Assert.Equal(variant == BuildVariant.Server, variant.IsHeadless());
        }
    }

    /// <summary>
    ///     Release is the only variant that gives up its assertions and its profiler. Development in
    ///     particular keeps both, which is the entire reason <c>docs/plan/17</c> gives it a row of
    ///     its own rather than treating it as release with a flag.
    /// </summary>
    [Fact]
    public void ReleaseIsTheOnlyOneThatGivesUpItsDiagnostics() {
        foreach (var variant in Enum.GetValues<BuildVariant>()) {
            Assert.Equal(variant != BuildVariant.Release, variant.HasValidation());
            Assert.Equal(variant != BuildVariant.Release, variant.HasDiagnostics());
        }
    }

    [Fact]
    public void AVariantNamedOnTheCommandLineIsReadCaseInsensitively() {
        Assert.Equal(BuildVariant.Development, AppArguments.Parse(["--vixen-variant", "development"]).Variant);
        Assert.Equal(BuildVariant.Server, AppArguments.Parse(["--vixen-variant", "SERVER"]).Variant);
    }

    /// <summary>The variant decides the platform without anybody passing a second flag.</summary>
    [Fact]
    public void AServerBuildIsHeadlessWithoutBeingToldTwice() {
        var config = new AppConfig();
        config.Apply(AppArguments.Parse(["--vixen-variant", "Server"]));

        Assert.Equal(BuildVariant.Server, config.Variant);
        Assert.True(config.Headless);
    }
}
