// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;
using Vixen.Ecs.Systems;

namespace Vixen.Engine.Coroutines;

/// <summary>Resumes coroutines waiting on <see cref="ResumePoint.Update" />.</summary>
/// <remarks>
///     Registered after the behaviour update pass, so a coroutine resumed this frame sees a world
///     that <c>Update</c> has already had its say about — the same order Unity puts its coroutine
///     pass in, and the one code written against either will assume. Like the behaviour passes it
///     declares no access, because a coroutine body is arbitrary user code and the only honest
///     reading of that is "assume everything".
/// </remarks>
/// <param name="scheduler">The scheduler to drain.</param>
[UpdateInGroup(SystemPhase.Update)]
public sealed class CoroutineUpdateSystem(CoroutineScheduler scheduler) : SystemBase {
    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        scheduler.Drain(ResumePoint.Update);
        return dependency;
    }
}

/// <summary>Resumes coroutines waiting on <see cref="ResumePoint.LateUpdate" />.</summary>
/// <param name="scheduler">The scheduler to drain.</param>
[UpdateInGroup(SystemPhase.LateUpdate)]
public sealed class CoroutineLateUpdateSystem(CoroutineScheduler scheduler) : SystemBase {
    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        scheduler.Drain(ResumePoint.LateUpdate);
        return dependency;
    }
}

/// <summary>Resumes coroutines waiting on <see cref="ResumePoint.FixedStep" />, once per step.</summary>
/// <remarks>
///     Counts the step itself rather than being told about it, because this system runs exactly once
///     per fixed step by construction — that is what <c>SystemPhase.FixedUpdate</c> means — and a
///     second thing that also had to be called once per step would be a second thing to forget.
/// </remarks>
/// <param name="scheduler">The scheduler to drain.</param>
[UpdateInGroup(SystemPhase.FixedUpdate)]
public sealed class CoroutineFixedStepSystem(CoroutineScheduler scheduler) : SystemBase {
    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        scheduler.BeginStep();
        scheduler.Drain(ResumePoint.FixedStep);
        return dependency;
    }
}

/// <summary>Resumes coroutines waiting on <see cref="ResumePoint.EndOfFrame" />.</summary>
/// <param name="scheduler">The scheduler to drain.</param>
[UpdateInGroup(SystemPhase.PostRender)]
public sealed class CoroutineEndOfFrameSystem(CoroutineScheduler scheduler) : SystemBase {
    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        scheduler.Drain(ResumePoint.EndOfFrame);
        return dependency;
    }
}
