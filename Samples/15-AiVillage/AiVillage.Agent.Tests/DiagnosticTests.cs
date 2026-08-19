// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;
using Vixen.Ai.Ecs;
using Vixen.Ai.Nodes;
using Vixen.Core;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Samples.AiVillage.Tests;

/// <summary>Scratch: what the village is actually doing, printed.</summary>
public class DiagnosticTests(ITestOutputHelper output) {
    [Fact]
    public void Dump() {
        using var run = new VillageRun();

        run.Until(12.0);

        var village = run.Village;
        var board = village.Agents.BlackboardOf(in village.World.Read<AiAgent>(village.Villager));

        output.WriteLine($"refuge key = {board?.GetVector3(village.Layout.Key("refuge"))}");
        output.WriteLine($"target set = {board?.IsSet(village.Layout.Key("target"))}");
        output.WriteLine($"age        = {board?.GetFloat(village.Layout.Key("age"))}");
        output.WriteLine($"guard  at {village.Where(village.Guard)} doing {village.Doing(village.Guard)}");
        output.WriteLine($"villager at {village.Where(village.Villager)} doing {village.Doing(village.Villager)}");
        output.WriteLine($"scav   at {village.Where(village.Scavenger)} doing {village.Doing(village.Scavenger)}");
        output.WriteLine($"intruder at {village.Where(village.Intruder)}");

        var guardBoard = village.Agents.BlackboardOf(in village.World.Read<AiAgent>(village.Guard));

        output.WriteLine($"guard age  = {guardBoard?.GetFloat(village.Layout.Key("age"))}");
        output.WriteLine($"guard tgt  = {guardBoard?.GetEntity(village.Layout.Key("target"))}");
        output.WriteLine($"perceived  = {village.Perception.PerceivedBy(village.World, village.Guard)?.Count}");
        output.WriteLine($"--- changes ---");
        output.WriteLine(run.Decisions.Transcript());
    }
}
