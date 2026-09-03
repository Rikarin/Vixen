// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0
//
// The browser half of Vixen.Audio.Backend.WebAudio, run under Node against a WebAudio stub.
//
//     node Platform/Vixen.Platform.Web.Tests/js/vixen-audio.test.mjs
//
// ── ⚠ Why this exists ────────────────────────────────────────────────────────────────────────
//
// captureRead built `new Float32Array(samples.buffer, samples.byteOffset, …)` over the MemoryView
// the marshaller hands it for a `[JSMarshalAs<JSType.MemoryView>] Span<byte>`. A MemoryView has
// neither member, so that threw `TypeError: First argument to Float32Array constructor must be an
// ArrayBuffer` on every microphone read on every browser build — the whole of
// WebAudioCaptureDevice.Read.
//
// It was recorded as needing "an audio device headless" before it could be verified. ⚠ It does
// not: the defect is at the marshalling boundary and throws before a sample is touched, so a
// faithful MemoryView (memory-view.mjs) and a ring buffer filled by hand are the whole apparatus.
// What a real device would add is latency and permission behaviour, neither of which is what was
// wrong.
//
// ── What this does NOT cover ─────────────────────────────────────────────────────────────────
//
// The output side's latency, which is the subject of the AudioWorklet work: the queue here is
// AudioBufferSourceNodes scheduled ahead, and its 40 ms is a property of the design and not a bug
// this could catch.

import { fileURLToPath, pathToFileURL } from "node:url";
import { dirname, join } from "node:path";
import { MemoryView } from "./memory-view.mjs";

// ── Assertions ───────────────────────────────────────────────────────────────────────────────

let passed = 0;

function check(condition, what) {
    if (!condition) {
        console.error(`FAILED: ${what}`);
        process.exit(1);
    }

    passed++;
}

function equal(actual, expected, what) {
    check(actual === expected, `${what} — expected ${expected}, got ${actual}`);
}

function near(actual, expected, what) {
    check(Math.abs(actual - expected) < 1e-6, `${what} — expected ${expected}, got ${actual}`);
}

function survives(what, body) {
    try {
        const value = body();
        passed++;
        return value;
    } catch (error) {
        console.error(`FAILED: ${what} — threw ${error}`);
        process.exit(1);
    }
}

// ── A WebAudio stub ──────────────────────────────────────────────────────────────────────────

const scheduled = [];

/** The most recent ScriptProcessorNode the module asked for, so the suite can drive it. */
let processor = null;

class AudioBuffer {
    constructor(channels, frames) {
        this.numberOfChannels = channels;
        this.length = frames;
        this.planes = Array.from({ length: channels }, () => new Float32Array(frames));
    }

    getChannelData(channel) {
        return this.planes[channel];
    }
}

class AudioContext {
    constructor(options) {
        // The stub honours the requested rate, which a real one is free not to — the module reads
        // it back through sampleRate() for exactly that reason.
        this.sampleRate = options?.sampleRate ?? 44100;
        this.currentTime = 0;
        this.destination = { __kind: "destination" };
    }

    createBuffer(channels, frames) {
        return new AudioBuffer(channels, frames);
    }

    createBufferSource() {
        const node = {
            buffer: null,
            onended: null,
            connect() { },
            disconnect() { },
            start(when) {
                node.startedAt = when;
                scheduled.push(node);
            },
            stop() { }
        };

        return node;
    }

    createMediaStreamSource() {
        return { connect() { }, disconnect() { } };
    }

    createScriptProcessor() {
        processor = { onaudioprocess: null, connect() { }, disconnect() { } };
        return processor;
    }

    resume() { }

    close() { }
}

globalThis.AudioContext = AudioContext;

/** One track, so captureStop has something to stop. */
const stream = { getTracks: () => [{ stop() { } }] };

Object.defineProperty(globalThis, "navigator", {
    // ⚠ defineProperty: Node's own `navigator` global is an accessor with no setter, so a plain
    // assignment throws and reads as a defect in the module under test.
    value: { mediaDevices: { getUserMedia: () => Promise.resolve(stream) } },
    writable: true,
    configurable: true
});

// ── The module ───────────────────────────────────────────────────────────────────────────────

const here = dirname(fileURLToPath(import.meta.url));

// A file URL and not the path — see the note in vixen-platform.test.mjs.
const audio = await import(
    pathToFileURL(join(here, "../../Vixen.Audio.Backend.WebAudio/wwwroot/vixen-audio.js")).href
);

// ── Playback: enqueue, which was already right ───────────────────────────────────────────────
//
// It calls samples.slice() first, and slice() is one of the five members a MemoryView really has —
// so this branch was correct before and is pinned here so a later "tidy-up" cannot quietly turn it
// back into `samples.buffer`.

const CHANNELS = 2;
const FRAMES = 4;

const playback = audio.create(48000, CHANNELS, FRAMES, 4);

check(playback > 0, "create opens an AudioContext");
equal(audio.sampleRate(playback), 48000, "…at the rate that was asked for");

// Interleaved L,R — the storage every other Vixen audio path uses.
const block = new Float32Array([1, -1, 2, -2, 3, -3, 4, -4]);
const asBytes = new Uint8Array(block.buffer.slice(0));

survives("enqueue reads a block out of a MemoryView", () =>
    audio.enqueue(playback, new MemoryView(asBytes), FRAMES)
);

equal(scheduled.length, 1, "…and schedules one source node for it");

const buffer = scheduled[0].buffer;

// ⚠ Deinterleaved on the way in. WebAudio is the one API in this engine that wants planar, and a
// deinterleave that silently transposed would be a stereo image swapped end to end.
near(buffer.getChannelData(0)[0], 1, "…left channel, first frame");
near(buffer.getChannelData(1)[0], -1, "…right channel, first frame");
near(buffer.getChannelData(0)[3], 4, "…left channel, last frame");
near(buffer.getChannelData(1)[3], -4, "…right channel, last frame — not a run of zeros");

// ── Capture: captureRead, which was not ──────────────────────────────────────────────────────

const capture = audio.captureCreate(48000, CHANNELS, 64);

check(capture > 0, "captureCreate opens a capture slot");
equal(audio.captureAvailable(capture), 0, "…with nothing buffered yet");

// ⚠ Filled through the module's own onaudioprocess rather than by reaching into its state. There
// is no test-only export here on purpose: a hatch that only a suite uses is a second code path,
// and the ring is reachable the way a microphone reaches it.
audio.captureStart(capture);

// captureStart's body runs inside a `.then`, so the processor does not exist until the microtask
// queue drains. Awaiting a resolved promise is enough and does not depend on a timer.
await Promise.resolve();
await Promise.resolve();

check(audio.captureIsRunning(capture), "captureStart runs once getUserMedia resolves");
check(processor !== null, "…and asks for a ScriptProcessorNode to pull from");

const input = new AudioBuffer(CHANNELS, 8);

for (let frame = 0; frame < 8; frame++) {
    input.getChannelData(0)[frame] = frame + 1;
    input.getChannelData(1)[frame] = -(frame + 1);
}

processor.onaudioprocess({ inputBuffer: input });

equal(audio.captureAvailable(capture), 8, "…and buffers eight frames when the node delivers them");

// The C# side passes its whole fixed transfer buffer and asks for a chunk, so the view is longer
// than what gets written — WebAudioCaptureDevice.Read, `transfer.AsSpan()` with `chunk`.
const transfer = new Uint8Array(16 * CHANNELS * 4);

const got = survives("captureRead writes through a MemoryView rather than reading .buffer off it", () =>
    audio.captureRead(capture, new MemoryView(transfer), 8)
);

equal(got, 8, "…returning how many frames it wrote");

const floats = new Float32Array(transfer.buffer, 0, 8 * CHANNELS);

// ⚠ Not merely "did not throw". The failure mode a MemoryView produces when it is handed to
// something array-like is a correctly sized run of ZEROS — and silence is exactly what a
// microphone that is not recording sounds like, so the length being right proves nothing.
near(floats[0], 1, "…the first sample, left");
near(floats[1], -1, "…the first sample, right");
near(floats[14], 8, "…and the last frame's left sample, rather than silence");
near(floats[15], -8, "…and its right");

equal(audio.captureAvailable(capture), 0, "…and the read advanced the ring");

// A second read with nothing buffered must refuse rather than throw or hand back stale frames.
equal(audio.captureRead(capture, new MemoryView(transfer), 8), 0, "an empty ring reads nothing");

console.log(`${passed} assertions passed`);
