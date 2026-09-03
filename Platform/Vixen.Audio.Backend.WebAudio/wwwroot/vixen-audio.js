// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0
//
// The browser half of Vixen.Audio.Backend.WebAudio.
//
// Vixen mixes in software, so none of WebAudio's graph is used: no PannerNode, no
// ConvolverNode, no GainNode automation. What arrives here is finished interleaved
// float frames, and the whole job is to get them to the speakers at the right time.
//
// The mechanism is a scheduled queue of AudioBufferSourceNodes, which is the one
// approach that works in every browser without SharedArrayBuffer, without
// cross-origin isolation headers, and without .NET threads — none of which a
// WebAssembly build can rely on having. An AudioWorklet would be lower latency and
// would need all three, because the worklet runs on the audio thread and cannot
// call into a single-threaded runtime.
//
// The cost is latency: a block cannot be scheduled later than "now", so the queue
// has to run ahead. Four 480-frame blocks at 48 kHz is 40 ms, which is the same
// figure the OpenAL backend queues and is not noticeable for anything but rhythm
// games.

const contexts = new Map();
let nextHandle = 1;

/** Opens an AudioContext. Returns a handle, or 0 if this browser has no WebAudio. */
export function create(sampleRate, channels, blockFrames, blockCount) {
    const Constructor = globalThis.AudioContext || globalThis.webkitAudioContext;

    if (!Constructor) {
        return 0;
    }

    let context;

    try {
        // Safari refuses rates it does not like rather than resampling, so an explicit
        // rate is a request and not a demand: the fallback is whatever the hardware runs
        // at, which the caller reads back through sampleRate().
        context = new Constructor({ sampleRate });
    } catch {
        context = new Constructor();
    }

    const handle = nextHandle++;

    contexts.set(handle, {
        context,
        channels,
        blockFrames,
        blockCount,
        nextTime: 0,
        underruns: 0,
        timer: 0,
        scheduled: []
    });

    return handle;
}

export function sampleRate(handle) {
    const state = contexts.get(handle);
    return state ? state.context.sampleRate : 0;
}

export function isRunning(handle) {
    const state = contexts.get(handle);
    return !!state && state.context.state === "running";
}

export function underruns(handle) {
    const state = contexts.get(handle);
    return state ? state.underruns : 0;
}

/** Resumes the context. A browser starts it suspended until a user gesture. */
export function resume(handle) {
    const state = contexts.get(handle);

    if (state) {
        state.context.resume();
    }
}

/**
 * Starts the clock. `pump(count)` is a .NET callback that renders `count` blocks and
 * calls enqueue() for each of them.
 */
export function start(handle, pump) {
    const state = contexts.get(handle);

    if (!state || state.timer) {
        return;
    }

    state.context.resume();
    state.nextTime = state.context.currentTime;

    // Half a block, so the timer fires at least twice inside every block it has to
    // fill. setInterval is throttled hard in a background tab, which is exactly when
    // an underrun does not matter and the queue catches up on the next tick.
    const period = Math.max(4, (state.blockFrames * 500) / state.context.sampleRate);

    state.timer = setInterval(() => {
        const now = state.context.currentTime;
        const horizon = now + (state.blockFrames * state.blockCount) / state.context.sampleRate;

        if (state.nextTime < now) {
            // The queue ran dry: everything scheduled has already been played and the
            // speakers had nothing. Counting it is what makes the diagnostics overlay
            // able to say so.
            state.underruns++;
            state.nextTime = now;
        }

        const blockSeconds = state.blockFrames / state.context.sampleRate;
        let due = 0;

        while (state.nextTime + due * blockSeconds < horizon) {
            due++;
        }

        if (due > 0) {
            pump(due);
        }
    }, period);
}

export function stop(handle) {
    const state = contexts.get(handle);

    if (!state) {
        return;
    }

    if (state.timer) {
        clearInterval(state.timer);
        state.timer = 0;
    }

    for (const node of state.scheduled) {
        try {
            node.stop();
        } catch {
            // Already finished. Stopping a node twice throws and means nothing.
        }
    }

    state.scheduled.length = 0;
}

/**
 * Takes one interleaved block and schedules it at the end of the queue.
 *
 * `samples` is a view onto WebAssembly memory holding the block's *bytes* — the .NET
 * marshaller defines memory views for byte, int and double and not for float, so the
 * floats travel as their own bytes and get a Float32Array put over them here. The
 * view is only valid for this call, hence the slice.
 *
 * ── ⚠ `samples` is a MemoryView, and that is NOT a typed array ─────────────────────────────────
 *
 * What a `[JSMarshalAs<JSType.MemoryView>] Span<byte>` arrives as has exactly five members —
 * `set(source, offset)`, `copyTo(target, from)`, `slice(start, end)`, `length`, `byteLength` —
 * and no more. No indexer, no `fill`, no `.buffer`, no `.byteOffset`, and `set` compares the
 * source's constructor by identity rather than converting.
 *
 * ⚠ So `slice()` on the line below is load-bearing and not a defensive copy: it is the only
 * member that yields something a `Float32Array` can be laid over, and `bytes` is a real typed
 * array precisely because it came from one. Reading `samples.buffer` directly — which is what
 * `captureRead` used to do — yields `undefined`, and `new Float32Array(undefined, …)` builds a
 * ZERO-LENGTH array that swallows every write in silence.
 */
export function enqueue(handle, samples, frames) {
    const state = contexts.get(handle);

    if (!state) {
        return;
    }

    const bytes = samples.slice();
    const floats = new Float32Array(bytes.buffer, bytes.byteOffset, frames * state.channels);
    const buffer = state.context.createBuffer(state.channels, frames, state.context.sampleRate);

    // Deinterleave. WebAudio is the one API in this engine that wants planar, which is
    // why AudioClip's documentation names copyToChannel as the exception that proves
    // interleaved is the right storage everywhere else.
    for (let channel = 0; channel < state.channels; channel++) {
        const target = buffer.getChannelData(channel);

        for (let frame = 0; frame < frames; frame++) {
            target[frame] = floats[frame * state.channels + channel];
        }
    }

    const node = state.context.createBufferSource();
    node.buffer = buffer;
    node.connect(state.context.destination);
    node.onended = () => {
        const index = state.scheduled.indexOf(node);

        if (index >= 0) {
            state.scheduled.splice(index, 1);
        }
    };

    node.start(state.nextTime);
    state.scheduled.push(node);
    state.nextTime += frames / state.context.sampleRate;
}

export function close(handle) {
    stop(handle);
    const state = contexts.get(handle);

    if (state) {
        state.context.close();
        contexts.delete(handle);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Capture.
//
// getUserMedia is asynchronous and gated on a permission prompt, so captureStart
// returns before anything is running and captureIsRunning is what says whether it
// did. A caller that treats "no audio yet" as a failure will be wrong on exactly
// the platform where the delay is longest.
//
// A ScriptProcessorNode and not an AudioWorklet, for the same reason the output
// side schedules AudioBufferSourceNodes: a worklet runs on the audio thread and
// cannot reach a single-threaded WebAssembly runtime, and the SharedArrayBuffer
// route needs cross-origin isolation headers a game cannot assume its host sets.
// ScriptProcessorNode is deprecated and works everywhere, which for now beats
// correct and unavailable.

const captures = new Map();
let nextCapture = 1;

/** Opens a capture slot. Returns a handle, or 0 if this browser has no WebAudio. */
export function captureCreate(sampleRate, channels, bufferedFrames) {
    const Constructor = globalThis.AudioContext || globalThis.webkitAudioContext;

    if (!Constructor || !globalThis.navigator?.mediaDevices?.getUserMedia) {
        return 0;
    }

    let context;

    try {
        context = new Constructor({ sampleRate });
    } catch {
        context = new Constructor();
    }

    const handle = nextCapture++;

    captures.set(handle, {
        context,
        channels,
        // Interleaved, like everything else that crosses this boundary.
        ring: new Float32Array(bufferedFrames * channels),
        read: 0,
        written: 0,
        overruns: 0,
        running: false,
        stream: null,
        source: null,
        processor: null
    });

    return handle;
}

export function captureSampleRate(handle) {
    const state = captures.get(handle);
    return state ? state.context.sampleRate : 0;
}

export function captureIsRunning(handle) {
    const state = captures.get(handle);
    return state ? state.running : false;
}

export function captureAvailable(handle) {
    const state = captures.get(handle);
    return state ? (state.written - state.read) / state.channels : 0;
}

export function captureOverruns(handle) {
    const state = captures.get(handle);
    return state ? state.overruns : 0;
}

export function captureStart(handle) {
    const state = captures.get(handle);

    if (!state || state.running || state.stream) {
        return;
    }

    // Marked before the promise resolves so that a second call cannot open a second
    // stream while the first is still being granted.
    state.stream = true;

    navigator.mediaDevices.getUserMedia({ audio: true, video: false }).then(stream => {
        if (!captures.has(handle)) {
            stream.getTracks().forEach(track => track.stop());
            return;
        }

        state.stream = stream;
        state.source = state.context.createMediaStreamSource(stream);

        // 2048 frames is about 43 ms at 48 kHz. Smaller buffers are permitted and are
        // where a ScriptProcessor starts glitching on a busy main thread, which is the
        // thread a WebAssembly game is already saturating.
        state.processor = state.context.createScriptProcessor(2048, state.channels, state.channels);

        state.processor.onaudioprocess = event => {
            const input = event.inputBuffer;
            const frames = input.length;
            const capacity = state.ring.length;
            const used = state.written - state.read;
            const room = (capacity - used) / state.channels;
            const taking = Math.min(frames, room);

            if (taking < frames) {
                state.overruns += frames - taking;
            }

            for (let frame = 0; frame < taking; frame++) {
                for (let channel = 0; channel < state.channels; channel++) {
                    const index = (state.written + (frame * state.channels) + channel) % capacity;
                    state.ring[index] = input.getChannelData(channel)[frame];
                }
            }

            state.written += taking * state.channels;
        };

        // Connected to the destination because a ScriptProcessorNode with no consumer is
        // not pulled by some browsers. The processor writes nothing to its output buffer,
        // so what reaches the speakers is silence — this is not monitoring.
        state.source.connect(state.processor);
        state.processor.connect(state.context.destination);
        state.context.resume();
        state.running = true;
    }).catch(() => {
        // Refused, or no microphone. Not running, and not an error anybody can act on
        // from here — the caller sees captureIsRunning stay false.
        state.stream = null;
    });
}

/**
 * Copies up to `frames` frames into the caller's view. Returns how many it wrote.
 *
 * ⚠ `samples` is a .NET MemoryView, NOT a typed array — see the note above `enqueue`. It has no
 * `.buffer` and no `.byteOffset`, so this stages the floats in a real Float32Array and hands them
 * over with one `set()`, whose source must be a `Uint8Array` because the span is `Span<byte>`.
 *
 * ⚠⚠ The version this replaces built `new Float32Array(samples.buffer, samples.byteOffset, …)`,
 * and that did NOT throw. `Float32Array`'s constructor treats a non-object first argument as a
 * *length*, and `Number(undefined)` is `NaN`, which becomes 0 — so it produced an EMPTY array,
 * discarded every write into it without complaint, and returned a positive frame count saying it
 * had filled the caller's buffer. Every microphone read on every browser build handed
 * WebAudioCaptureDevice whatever was already in its transfer buffer, and reported success.
 *
 * ⚠ That is worse than the four sites in vixen-webgpu.js, which throw a visible TypeError out of
 * `new DataView(...)`. Silence is what a microphone that is not recording sounds like, so this one
 * had no symptom at all. It is the reason the assertions in vixen-audio.test.mjs check the sample
 * VALUES and not just that the call returned.
 */
export function captureRead(handle, samples, frames) {
    const state = captures.get(handle);

    if (!state) {
        return 0;
    }

    const capacity = state.ring.length;
    const available = (state.written - state.read) / state.channels;
    const taking = Math.min(frames, available);

    if (taking <= 0) {
        return 0;
    }

    const count = taking * state.channels;
    const floats = new Float32Array(count);

    for (let i = 0; i < count; i++) {
        floats[i] = state.ring[(state.read + i) % capacity];
    }

    // The floats travel as their own bytes, because the marshaller defines memory views for byte,
    // int and double and not for float — the same reason `enqueue` receives bytes.
    samples.set(new Uint8Array(floats.buffer, 0, floats.byteLength), 0);

    state.read += count;
    return taking;
}

export function captureStop(handle) {
    const state = captures.get(handle);

    if (!state) {
        return;
    }

    state.running = false;

    if (state.processor) {
        state.processor.onaudioprocess = null;
        state.processor.disconnect();
        state.processor = null;
    }

    if (state.source) {
        state.source.disconnect();
        state.source = null;
    }

    if (state.stream && state.stream !== true) {
        state.stream.getTracks().forEach(track => track.stop());
    }

    state.stream = null;
}

export function captureClose(handle) {
    captureStop(handle);
    const state = captures.get(handle);

    if (state) {
        state.context.close();
        captures.delete(handle);
    }
}
