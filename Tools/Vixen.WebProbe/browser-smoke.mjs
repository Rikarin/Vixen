// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0
//
// The browser smoke leg: serves a published head, drives a real Chromium over CDP, and fails if
// the [JSImport] boundary or the frame loop did not actually work.
//
//     node Tools/Vixen.WebProbe/browser-smoke.mjs artifacts/web/wwwroot
//
// `nuke BrowserSmoke` is this file with the publish in front of it. Exit code 0 means every check
// below ran and passed; every other path out of here is a non-zero exit with a named reason.
//
// ── Why a hand-written CDP client and not Playwright ─────────────────────────────────────────
//
// docs/plan/10 § Platform CI matrix names Playwright, and the row in docs/overview.md was written
// with that name in it. What is actually needed is narrower than what Playwright is: one page, one
// navigation, a console transcript, two Runtime.evaluate calls and two synthesised input events.
// That is about 200 lines of CDP over a WebSocket, and Node has had a global WebSocket since 22.
//
// Against that, `playwright-core` is a third-party npm dependency in a repository whose
// dependencies are attributed by a gate (`nuke CheckAttribution`, docs/manual/third-party.md) that
// reads Directory.Packages.props and native-dependencies.json — and would therefore NOT see an npm
// package at all. Adding one would create the exact class of unattributed dependency that gate was
// written to make impossible, and it would do it in the one place the gate cannot look. The other
// JavaScript check in this repository (Vixen.Platform.Web.Tests/js/vixen-platform.test.mjs) makes
// the same call in its own header: "No dependencies, no package.json, no install step."
//
// So: no npm install, no vendored binary, no lockfile. The browser itself is not vendored either —
// it is the Chrome already on the runner (GitHub's ubuntu-latest image ships one), located below,
// and a missing browser is a FAILURE rather than a skip.
//
// ── ⚠ Why it drives a browser rather than dumping the DOM ────────────────────────────────────
//
// `chrome-headless-shell --dump-dom` NEVER FIRES requestAnimationFrame — measured in
// docs/plan/spikes/web-head/RESULT.md, with and without --virtual-time-budget, --screenshot and
// SwiftShader: a pure-JS control page counted zero callbacks in three seconds, while the same page
// over CDP counted 120/s. A leg built on --dump-dom would report a live frame loop as dead. This is
// also why `checkInstrument` below counts rAF from the driver's own side before it believes
// anything the page says about frames: if the browser is not animating, that is an instrument
// failure and it is reported as one, not as a broken engine.

import { createServer } from 'node:http';
import { readFile, mkdtemp, rm } from 'node:fs/promises';
import { existsSync, readFileSync } from 'node:fs';
import { extname, join, normalize, resolve } from 'node:path';
import { spawn } from 'node:child_process';
import { tmpdir } from 'node:os';
import process from 'node:process';

// ── The ledger ───────────────────────────────────────────────────────────────────────────────
//
// ⚠ Every claim this file makes goes through here, and the count is asserted at the end against a
// floor. A run that reaches the summary having executed no checks is the failure mode this
// repository has been bitten by twice — a content-bytes comparator that called three EMPTY
// manifests "identical bytes" and exited 0, and eighteen golden files that PASSED without a
// device. `zero checks` must not be able to look like `all checks passed`.

const ledger = [];

function check(name, ok, detail) {
    ledger.push({ name, ok: !!ok, detail: detail ?? '' });
    console.log(`  ${ok ? 'pass' : 'FAIL'}  ${name}${detail ? '  — ' + detail : ''}`);
}

/** A failure the leg cannot continue past: no browser, no page, no transcript. */
class SmokeError extends Error { }

// ── Arguments ────────────────────────────────────────────────────────────────────────────────

const argv = process.argv.slice(2);
const positional = argv.filter(a => !a.startsWith('--'));
const flag = (name, fallback) => {
    const hit = argv.find(a => a.startsWith(`--${name}=`));
    return hit === undefined ? fallback : hit.slice(name.length + 3);
};

const siteRoot = resolve(positional[0] ?? 'artifacts/web/wwwroot');
const timeoutMs = Number(flag('timeout', 90_000));

/** Where the synthesised pointer is aimed, in VIEWPORT coordinates. See the check that uses it. */
const pointerAt = [42, 24];

// How many checks this leg is expected to execute. Named rather than derived, precisely so that a
// probe that silently stopped reporting half of them cannot pass by reporting none.
const minimumChecks = Number(flag('minimum-checks', 20));

// ── Finding a browser ────────────────────────────────────────────────────────────────────────
//
// Order: an explicit VIXEN_CHROME, then the variables the runner images set, then the usual paths.
// ⚠ chrome-headless-shell is deliberately NOT in this list. It is a different binary from Chrome
// and it is the one the spike measured not firing requestAnimationFrame; naming it here would let
// a machine that has both pick the one that cannot answer the question this leg asks.

const browserCandidates = [
    process.env.VIXEN_CHROME,
    process.env.CHROME_PATH,
    process.env.CHROME_BIN,
    '/usr/bin/google-chrome',
    '/usr/bin/google-chrome-stable',
    '/usr/bin/chromium',
    '/usr/bin/chromium-browser',
    '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
    '/Applications/Chromium.app/Contents/MacOS/Chromium',
    join(
        process.env.HOME ?? '',
        'Library/Caches/ms-playwright/chromium-1228/chrome-mac-arm64/Google Chrome for Testing.app'
        + '/Contents/MacOS/Google Chrome for Testing'
    )
].filter(Boolean);

function findBrowser() {
    for (const candidate of browserCandidates) {
        if (existsSync(candidate)) return candidate;
    }

    throw new SmokeError(
        'no Chrome or Chromium was found, so the browser smoke leg has nothing to drive. This is a '
        + 'FAILURE and not a skip: a leg that quietly passes when it did not start a browser is '
        + 'worse than no leg. Set VIXEN_CHROME to a Chrome binary, or install one. Looked at:\n  '
        + browserCandidates.join('\n  ')
    );
}

// ── The static server ────────────────────────────────────────────────────────────────────────
//
// The same headers Tools/Vixen.WebProbe/serve.mjs sets, and for the same reason: without COOP/COEP
// the page is not cross-origin isolated and navigator.hardwareConcurrency reports 1. That is
// asserted below, so this server's headers are themselves under test.

const contentTypes = {
    '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript',
    '.wasm': 'application/wasm', '.json': 'application/json', '.dat': 'application/octet-stream',
    '.dll': 'application/octet-stream', '.pdb': 'application/octet-stream',
    '.css': 'text/css', '.txt': 'text/plain', '.blat': 'application/octet-stream'
};

function startServer(root) {
    const misses = [];

    const server = createServer(async (request, response) => {
        const url = new URL(request.url, 'http://localhost');
        let path = decodeURIComponent(url.pathname);
        if (path.endsWith('/')) path += 'index.html';
        const file = join(root, normalize(path).replace(/^(\.\.[/\\])+/, ''));

        try {
            const body = await readFile(file);
            response.writeHead(200, {
                'Content-Type': contentTypes[extname(file)] ?? 'application/octet-stream',
                'Cross-Origin-Opener-Policy': 'same-origin',
                'Cross-Origin-Embedder-Policy': 'require-corp',
                'Cache-Control': 'no-store'
            });
            response.end(body);
        } catch {
            // ⚠ /favicon.ico is not counted. The browser asks for it on its own, unprompted by
            // anything the page did, and a head that ships no icon is not a defect — counting it
            // made the very first end-to-end run of this leg fail on the browser's own habit.
            // Every other miss IS counted: a 404 on a .wasm, a .js or an asset is exactly the
            // shape of defect 3 in docs/plan/spikes/web-head/RESULT.md, where the page fetched its
            // own entry point, got nothing, and did nothing, with no build error anywhere.
            if (path !== '/favicon.ico') {
                misses.push(path);
            }

            response.writeHead(404, { 'Content-Type': 'text/plain' });
            response.end('not found: ' + path);
        }
    });

    return new Promise(resolveServer => {
        // Port 0: the OS picks. Two of these legs on one runner must not collide, and a hard-coded
        // port is the flake nobody reproduces.
        server.listen(0, '127.0.0.1', () => resolveServer({
            server,
            port: server.address().port,
            misses
        }));
    });
}

// ── A CDP client, in about eighty lines ──────────────────────────────────────────────────────

class Cdp {
    #socket;
    #nextId = 1;
    #pending = new Map();
    #handlers = [];

    static async connect(url) {
        const client = new Cdp();
        client.#socket = new WebSocket(url);

        await new Promise((ok, bad) => {
            client.#socket.addEventListener('open', ok, { once: true });
            client.#socket.addEventListener('error', () => bad(
                new SmokeError(`could not open a CDP WebSocket to ${url}`)
            ), { once: true });
        });

        client.#socket.addEventListener('message', event => {
            const message = JSON.parse(event.data);

            if (message.id !== undefined) {
                const waiter = client.#pending.get(message.id);
                client.#pending.delete(message.id);
                if (!waiter) return;

                if (message.error) {
                    waiter.bad(new SmokeError(`${waiter.method}: ${message.error.message}`));
                } else {
                    waiter.ok(message.result);
                }

                return;
            }

            for (const handler of client.#handlers) handler(message);
        });

        return client;
    }

    on(handler) { this.#handlers.push(handler); }

    send(method, params = {}, sessionId) {
        const id = this.#nextId++;
        const payload = { id, method, params };
        if (sessionId) payload.sessionId = sessionId;
        this.#socket.send(JSON.stringify(payload));

        return new Promise((ok, bad) => this.#pending.set(id, { ok, bad, method }));
    }

    close() { try { this.#socket.close(); } catch { /* already gone */ } }
}

/** Resolves when `predicate` sees a matching event, or rejects with `whatWasMissing`. */
function waitFor(client, predicate, whatWasMissing, budgetMs) {
    return new Promise((ok, bad) => {
        const timer = setTimeout(
            () => bad(new SmokeError(whatWasMissing)),
            budgetMs
        );

        client.on(message => {
            if (predicate(message)) {
                clearTimeout(timer);
                ok(message);
            }
        });
    });
}

const sleep = ms => new Promise(ok => setTimeout(ok, ms));

/** Waits for a console line the page prints. Rejects, with the transcript, when it never comes. */
function waitForLine(lines, prefix, whatWasMissing, budgetMs, transcriptOf) {
    return new Promise((ok, bad) => {
        const give_up = async () => {
            clearInterval(poll);
            bad(new SmokeError(`${whatWasMissing}\n${await transcriptOf()}`));
        };

        const timer = setTimeout(give_up, budgetMs);

        const poll = setInterval(() => {
            const hit = lines.find(line => line.text.startsWith(prefix));
            if (!hit) return;
            clearTimeout(timer);
            clearInterval(poll);
            ok(hit.text);
        }, 100);
    });
}

/** The highest frame count the page has printed so far, or -1 if it has printed none. */
function framesSoFar(lines) {
    let highest = -1;

    for (const line of lines) {
        const match = /^VIXENPROBE frames=(\d+)/.exec(line.text);
        if (match) highest = Math.max(highest, Number(match[1]));
    }

    return highest;
}

// ── The run ──────────────────────────────────────────────────────────────────────────────────

async function main() {
    if (!existsSync(join(siteRoot, 'index.html'))) {
        throw new SmokeError(
            `there is no index.html under '${siteRoot}', so there is no published head to drive. `
            + 'Run `./build.sh PublishWeb --configuration Release` first, or point this script at '
            + 'the site root of a publish (the wwwroot/ inside the output, not the output).'
        );
    }

    const browserPath = findBrowser();
    console.log(`browser   ${browserPath}`);

    const { server, port, misses } = await startServer(siteRoot);
    const pageUrl = `http://127.0.0.1:${port}/index.html`;
    console.log(`serving   ${siteRoot} on ${port}`);

    const profile = await mkdtemp(join(tmpdir(), 'vixen-smoke-'));

    // ⚠ --no-sandbox and --disable-dev-shm-usage are LINUX-ONLY here, and that is measured rather
    // than cargo-culted. On a CI container running as root the sandbox cannot start and /dev/shm is
    // 64 MB, so both are needed. On macOS arm64 they are not merely unnecessary: passing them
    // crashed Chrome 149 outright — `Received signal 10 BUS_ADRALN` in the browser process,
    // immediately after it had printed `DevTools listening on ws://…`, with every renderer then
    // reporting `Mach rendezvous failed … (parent died?)`. A leg that always passed them would be
    // undebuggable on the machine most people write this code on.
    const onLinux = process.platform === 'linux';
    const onWindows = process.platform === 'win32';

    const browserArguments = [
        '--headless=new',
        ...(onLinux ? ['--no-sandbox', '--disable-dev-shm-usage'] : []),
        '--enable-unsafe-swiftshader',
        '--no-first-run',
        '--no-default-browser-check',
        '--disable-extensions',
        // rAF is what this leg measures, and Chromium throttles it in a window it believes nobody
        // is looking at — which is every window in a headless run. Without these the frame check
        // would be measuring the compositor's opinion of the tab rather than the engine's loop.
        '--disable-background-timer-throttling',
        '--disable-renderer-backgrounding',
        '--disable-backgrounding-occluded-windows',
        '--window-size=800,600',
        `--user-data-dir=${profile}`,
        '--remote-debugging-port=0',
        'about:blank'
    ];

    // ⚠ THROUGH A SHELL THAT `exec`s, AND THIS IS NOT DECORATION. Measured on macOS 25.6 arm64
    // with Chrome for Testing 149: `spawn(chrome, args)` directly from Node starts the browser,
    // lets it print `DevTools listening on ws://…`, and then kills it with
    // `Received signal 10 BUS_ADRALN` before it will answer a single request — every renderer then
    // logs `Mach rendezvous failed, terminating process (parent died?)`. The same binary with the
    // same arguments launched from a shell is fine. Bisected: not the flags (nine combinations all
    // work from bash), not the stdio shape (pipes, an inherited fd and 'ignore' all fail), not
    // `detached`. The one thing that changes it is whether the immediate exec came from libuv's
    // posix_spawn or from a shell — three failures and three successes, deterministic.
    //
    // `sh -c 'exec "$0" "$@"'` keeps the process tree identical (the shell is replaced, so the
    // browser is still Node's direct child and still dies with it) and needs no quoting at all,
    // because the arguments travel as $0 and $@ rather than through a command string.
    const chrome = onWindows
        ? spawn(browserPath, browserArguments, { stdio: ['ignore', 'pipe', 'pipe'] })
        : spawn(
            '/bin/sh',
            ['-c', 'exec "$0" "$@"', browserPath, ...browserArguments],
            { stdio: ['ignore', 'pipe', 'pipe'] }
        );

    const chromeStderr = [];
    chrome.stderr.on('data', chunk => chromeStderr.push(String(chunk)));

    let exited = null;
    chrome.on('exit', code => { exited = code; });

    const cleanup = async () => {
        try { chrome.kill('SIGKILL'); } catch { /* already gone */ }
        server.close();
        await rm(profile, { recursive: true, force: true }).catch(() => { });
    };

    try {
        // ⚠ The port comes out of the profile directory rather than out of a guess. Asking for a
        // fixed port and finding it taken is how this kind of script fails on a busy runner and
        // then passes on a rerun.
        // ⚠ The existence of DevToolsActivePort is NOT the signal — its CONTENTS are. Chrome
        // creates the file and writes to it afterwards, so a reader that trusts existsSync gets an
        // empty string, builds the URL `http://127.0.0.1:/json/version`, and reports `fetch
        // failed` — which reads like a browser that never started and is nothing of the kind.
        // Measured here on the first run of this script. So: wait for a number, then wait for the
        // endpoint that number names to answer.
        const portFile = join(profile, 'DevToolsActivePort');
        const deadline = Date.now() + 30_000;

        let version = null;
        let lastError = '(never got as far as an error)';

        while (version === null) {
            if (exited !== null) {
                throw new SmokeError(
                    `the browser exited with ${exited} before it opened a debugging port. Its `
                    + `stderr:\n${chromeStderr.join('') || '(nothing)'}`
                );
            }

            if (Date.now() > deadline) {
                throw new SmokeError(
                    'the browser started but never answered on a debugging port within 30 s, so '
                    + `there is nothing to drive. Last attempt: ${lastError}. Its stderr:\n`
                    + (chromeStderr.join('') || '(nothing)')
                );
            }

            await sleep(100);

            if (!existsSync(portFile)) {
                lastError = 'DevToolsActivePort has not been written yet';
                continue;
            }

            const debugPort = readFileSync(portFile, 'utf8').split('\n')[0].trim();

            if (!/^\d+$/.test(debugPort)) {
                lastError = `DevToolsActivePort holds '${debugPort}', which is not a port`;
                continue;
            }

            try {
                version = await (await fetch(`http://127.0.0.1:${debugPort}/json/version`)).json();
            } catch (error) {
                lastError = `GET /json/version on ${debugPort}: ${error.message}`;
            }
        }

        console.log(`cdp       ${version.Browser}`);

        const client = await Cdp.connect(version.webSocketDebuggerUrl);

        const { targetId } = await client.send('Target.createTarget', { url: 'about:blank' });
        const { sessionId } = await client.send('Target.attachToTarget', { targetId, flatten: true });

        // ── The transcript ───────────────────────────────────────────────────────────────────
        const consoleLines = [];
        const pageErrors = [];
        const failedRequests = [];
        let documentStatus = null;

        client.on(message => {
            if (message.sessionId !== sessionId) return;

            if (message.method === 'Runtime.consoleAPICalled') {
                const text = message.params.args
                    .map(a => a.value ?? a.description ?? a.unserializableValue ?? '')
                    .join(' ');
                consoleLines.push({ type: message.params.type, text });
                return;
            }

            if (message.method === 'Runtime.exceptionThrown') {
                const d = message.params.exceptionDetails;
                pageErrors.push(d.exception?.description ?? d.text);
                return;
            }

            if (message.method === 'Network.responseReceived'
                && message.params.type === 'Document'
                && documentStatus === null) {
                documentStatus = message.params.response.status;
                return;
            }

            if (message.method === 'Network.loadingFailed') {
                failedRequests.push(message.params.errorText);
            }
        });

        await client.send('Runtime.enable', {}, sessionId);
        await client.send('Log.enable', {}, sessionId);
        await client.send('Network.enable', {}, sessionId);
        await client.send('Page.enable', {}, sessionId);

        const loaded = waitFor(
            client,
            m => m.sessionId === sessionId && m.method === 'Page.loadEventFired',
            `the page at ${pageUrl} never fired its load event`,
            30_000
        );

        await client.send('Page.navigate', { url: pageUrl }, sessionId);
        await loaded;

        // ⚠ Dumped on the way out of every timeout, and it is not belt and braces.
        // wwwroot/main.js installs `window.onerror` and `unhandledrejection` handlers that write
        // into #result and DO NOT call console.log — and both of the ways this page dies take that
        // route: a boot that throws inside WebPlatform.CreateAsync, and an exception out of a frame
        // callback, which WebFrameLoop deliberately rethrows on the browser's task queue rather
        // than through the interop boundary. Without this, the transcript for either says only
        // "the page printed nothing at all". Measured: with a [JSImport] pointed at a name the
        // module does not export, this is the difference between that sentence and the name.
        const resultPane = async () => {
            try {
                const value = await client.send('Runtime.evaluate', {
                    expression: "document.getElementById('result')?.textContent ?? '(no #result)'",
                    returnByValue: true
                }, sessionId);

                return '\n── the page\'s #result pane ─────────────────────────────────────\n'
                    + String(value.result.value).split('\n').map(line => '  ' + line).join('\n');
            } catch {
                return '\n(the #result pane could not be read)';
            }
        };

        const fullTranscript = async () =>
            transcript(consoleLines, pageErrors, failedRequests, misses) + await resultPane();

        // ── ⚠ Verify the instrument, before believing anything the page says ─────────────────
        //
        // This is the check the whole leg turns on. A harness built to prove the frame loop runs
        // will happily "prove" it in a browser that is not animating at all — and the browser mode
        // this repository already measured, chrome-headless-shell --dump-dom, is exactly that
        // browser. So the driver counts requestAnimationFrame from its own side, in its own page
        // context, over a second, with nothing of ours involved. If this is zero, every frame
        // claim below is meaningless and the run says so in those words rather than blaming the
        // engine.
        const driverRaf = await client.send('Runtime.evaluate', {
            expression: `new Promise(done => {
                let n = 0;
                const step = () => { n++; requestAnimationFrame(step); };
                requestAnimationFrame(step);
                setTimeout(() => done(n), 1000);
            })`,
            awaitPromise: true,
            returnByValue: true
        }, sessionId);

        const rafPerSecond = driverRaf.result.value;

        if (!(rafPerSecond > 0)) {
            throw new SmokeError(
                `INSTRUMENT FAILURE: requestAnimationFrame fired ${rafPerSecond} times in one `
                + 'second in this browser, measured by the driver against a plain JavaScript page '
                + 'with none of our code in it. This browser cannot answer the question this leg '
                + 'asks, so nothing below would have meant anything. See '
                + 'docs/plan/spikes/web-head/RESULT.md: chrome-headless-shell --dump-dom has '
                + 'exactly this signature. Do not read this as a broken frame loop.'
            );
        }

        console.log(`instrument  rAF ${rafPerSecond}/s in this browser (a live compositor)\n`);

        // ── Input, so that the event ring has something real in it ───────────────────────────
        //
        // A synthesised trusted event through the browser's own input pipeline, which is the only
        // way to put a record into vixen-platform.js's ring from outside. What it covers is the
        // [JSMarshalAs<JSType.MemoryView>] on drainEvents in BOTH directions — the managed side
        // hands over a Span<double>, JavaScript writes twelve doubles per record into it — plus
        // the record layout those two duplicate independently. That is a translation no C# test
        // can see, and until this leg nothing executed it.
        //
        // ⚠ It waits for a line the page prints rather than for a duration. The runtime is tens of
        // megabytes and a cold runner takes seconds over it; input dispatched before the module's
        // document listeners are attached lands nowhere, and the leg would fail on a machine that
        // was merely slow. docs/overview.md's flake row says timing-based waits are this
        // repository's commonest flake source, so there is not one here.
        await waitForLine(
            consoleLines,
            'VIXENPROBE ready-for-input',
            `the page never printed 'VIXENPROBE ready-for-input' within ${timeoutMs} ms, so the `
            + 'probe did not finish booting. Nothing was dispatched at it.',
            timeoutMs,
            fullTranscript
        );

        for (const [type, extra] of [
            ['mouseMoved', {}],
            ['mousePressed', { button: 'left', clickCount: 1 }],
            ['mouseReleased', { button: 'left', clickCount: 1 }]
        ]) {
            await client.send('Input.dispatchMouseEvent', {
                type, x: pointerAt[0], y: pointerAt[1], button: 'none', buttons: 0, ...extra
            }, sessionId);
        }

        await client.send('Input.dispatchKeyEvent', {
            type: 'keyDown', windowsVirtualKeyCode: 65, key: 'a', code: 'KeyA', text: 'a'
        }, sessionId);
        await client.send('Input.dispatchKeyEvent', {
            type: 'keyUp', windowsVirtualKeyCode: 65, key: 'a', code: 'KeyA'
        }, sessionId);

        // ── Wait for the page's own verdict ──────────────────────────────────────────────────
        const doneLine = await waitForLine(
            consoleLines,
            'VIXENPROBE done',
            `the page never printed 'VIXENPROBE done' within ${timeoutMs} ms. It booted and then `
            + 'either threw, hung, or never reached the end of its checks.',
            timeoutMs,
            fullTranscript
        );

        // ── What the page reported ───────────────────────────────────────────────────────────

        const probeChecks = consoleLines
            .map(l => /^VIXENPROBE check (\S+) (pass|fail)(?: (.*))?$/.exec(l.text))
            .filter(Boolean)
            .map(m => ({ name: m[1], ok: m[2] === 'pass', detail: m[3] ?? '' }));

        console.log('checks reported by the page:');

        for (const reported of probeChecks) {
            check(`page/${reported.name}`, reported.ok, reported.detail);
        }

        console.log('\nchecks made by the driver:');

        // The page counts its own checks and says so on the done line. If that number and the
        // number of `check` lines the driver actually saw disagree, some of the transcript was
        // lost — which would otherwise look like a shorter, greener run.
        const declared = /checks=(\d+) failed=(\d+)/.exec(doneLine);

        check(
            'transcript is complete',
            declared !== null && Number(declared[1]) === probeChecks.length,
            declared === null
                ? `the done line does not carry a count: '${doneLine}'`
                : `page declared ${declared[1]}, driver saw ${probeChecks.length}`
        );

        check(
            'page reported no failing check',
            declared !== null && Number(declared[2]) === 0,
            `failed=${declared?.[2] ?? '?'}`
        );

        check(
            'the document was served, not 404',
            documentStatus === 200,
            `HTTP ${documentStatus}`
        );

        check(
            'nothing 404d while loading',
            misses.length === 0,
            misses.length ? misses.join(', ') : 'no misses'
        );

        check(
            'no request failed',
            failedRequests.length === 0,
            failedRequests.length ? failedRequests.join(', ') : 'none'
        );

        check(
            'the page threw nothing',
            pageErrors.length === 0,
            pageErrors.length ? pageErrors[0] : 'no uncaught exception'
        );

        check(
            'requestAnimationFrame fires in this browser',
            rafPerSecond > 0,
            `${rafPerSecond}/s, measured by the driver`
        );

        // ── Two claims whose ends are in different processes ─────────────────────────────────
        //
        // The managed side set IWindow.Title, which is WebInterop.SetTitle, which is
        // `document.title = …` in vixen-platform.js. Reading it back here is the one assertion in
        // this leg that a value went all the way out of WebAssembly, through the marshaller, into
        // the DOM, and was observed by something that is not the page.
        const title = await client.send('Runtime.evaluate', {
            expression: 'document.title', returnByValue: true
        }, sessionId);

        check(
            'a managed [JSImport] reached the DOM',
            title.result.value === 'vixen-smoke-title',
            `document.title is '${title.result.value}'`
        );

        // The other half of the canvas-selector claim, and the half the page cannot make: the
        // selector JavaScript handed back to managed code has to address the canvas managed code
        // asked for. It is `[data-vixen-canvas="N"]` rather than the page's own `#view`, so a page
        // comparing it against a literal would be asserting its own markup. Resolved here instead.
        const observedSelector = consoleLines
            .map(line => /^VIXENPROBE observe canvas-selector (.+)$/.exec(line.text))
            .filter(Boolean)
            .map(match => match[1])
            .at(-1);

        const sameElement = observedSelector === undefined ? { result: { value: false } }
            : await client.send('Runtime.evaluate', {
                expression: `document.querySelector(${JSON.stringify(observedSelector)}) `
                    + '=== document.getElementById("view")',
                returnByValue: true
            }, sessionId);

        check(
            'the canvas selector managed code got addresses the right element',
            sameElement.result.value === true,
            observedSelector === undefined
                ? 'the page never reported one'
                : `${observedSelector} resolves to #view`
        );

        // The authority for the check the page can only infer. WebProcessors is internal and
        // IProcessorTopology has no IsCrossOriginIsolated, so the page infers isolation from a
        // processor count above one — which a single-core machine would fail while isolated. This
        // reads the browser's own flag, and it is also the direct test of this file's COOP/COEP.
        const isolated = await client.send('Runtime.evaluate', {
            expression: 'globalThis.crossOriginIsolated === true', returnByValue: true
        }, sessionId);

        check(
            'the page is cross-origin isolated',
            isolated.result.value === true,
            'COOP and COEP arrived on the response'
        );

        // ⚠ The coordinate translation, which is the one piece of arithmetic in the whole event
        // path and the one nothing else can check. This driver dispatched a pointer at (42, 24) in
        // VIEWPORT coordinates; vixen-platform.js is supposed to report it relative to the CANVAS,
        // which sits inside the page by the body's margin. So the expected answer is not a
        // constant — it is the dispatched point minus the element's rectangle, read here, at the
        // moment it is read. A module that forgot to subtract would report (42, 24) and pass any
        // check written against a literal.
        const rect = await client.send('Runtime.evaluate', {
            expression: 'JSON.stringify(document.getElementById("view").getBoundingClientRect())',
            returnByValue: true
        }, sessionId);

        const box = JSON.parse(rect.result.value);
        const reported = (consoleLines
            .map(line => /^VIXENPROBE observe pointer-position (-?[\d.]+),(-?[\d.]+)$/.exec(line.text))
            .filter(Boolean)
            .at(-1)) ?? null;

        const expected = [pointerAt[0] - box.left, pointerAt[1] - box.top];

        check(
            'the pointer position is canvas-relative, not viewport-relative',
            reported !== null
            && Math.abs(Number(reported[1]) - expected[0]) < 1.5
            && Math.abs(Number(reported[2]) - expected[1]) < 1.5,
            reported === null
                ? 'the page never reported one'
                : `dispatched (${pointerAt}) at a canvas whose origin is (${box.left}, ${box.top}); `
                + `expected (${expected}), engine said (${reported[1]}, ${reported[2]})`
        );

        // ── ⚠ The frame loop, measured twice and from outside ────────────────────────────────
        //
        // The page prints its frame count as it goes; that alone would be satisfied by a probe
        // that printed a constant. So the driver takes the highest count it has seen, waits a
        // second, and takes it again — and requires it to have MOVED. A loop that started and
        // stopped, which is exactly what defect 2 (`dotnet.run()` instead of `runMain()`)
        // produced and what `nuke PublishWeb` can only guess at from a published file's shape,
        // passes every check above and fails this one.
        const framesBefore = framesSoFar(consoleLines);
        await sleep(1000);
        const framesAfter = framesSoFar(consoleLines);

        check(
            'the page reported a frame count at all',
            framesBefore >= 0,
            `highest frames= line seen: ${framesBefore}`
        );

        check(
            'the managed frame loop is still ticking a second later',
            framesAfter > framesBefore,
            `${framesBefore} → ${framesAfter} in 1 s`
        );

        // ⚠ A floor, and a deliberately low one. The rate is the display's — a software-composited
        // headless runner is under no obligation to hit 60 — and the page reports every 30th frame,
        // so the observable delta over a second is quantised to multiples of 30. What this floor
        // is against is the only number a dead loop can produce, which is zero.
        check(
            'the frame loop ran at a plausible rate',
            framesAfter - framesBefore >= 15,
            `${framesAfter - framesBefore} frames in 1 s (reported in blocks of 30), floor 15`
        );

        // ── The summary ──────────────────────────────────────────────────────────────────────
        //
        // The page is still open here on purpose: a failing check wants the #result pane in its
        // transcript, and reading that needs a live session.

        const failed = ledger.filter(entry => !entry.ok);

        console.log('');

        // ⚠ THE CHECK ON THE CHECKS. Every number above could be right and this leg still prove
        // nothing, if the reason it saw no failure is that it made no assertion. So the count is
        // itself asserted, against a floor named on the command line. This is the answer to "what
        // does this gate print on the day it does not run": it prints a failure.
        if (ledger.length < minimumChecks) {
            throw new SmokeError(
                `the leg executed only ${ledger.length} check(s), under the ${minimumChecks} it is `
                + 'required to execute. Nothing failed, and that is exactly the problem: a run that '
                + 'asserts nothing must not be able to look like a run that asserted everything. '
                + 'Either the probe stopped reporting checks or --minimum-checks is stale.'
            );
        }

        if (failed.length > 0) {
            console.log(`FAILED — ${failed.length} of ${ledger.length} checks:`);
            for (const entry of failed) console.log(`  ${entry.name}: ${entry.detail}`);
            console.log(await fullTranscript());
            return 1;
        }

        await client.send('Target.closeTarget', { targetId }).catch(() => { });
        client.close();

        console.log(
            `OK — ${ledger.length} checks, all passed. The [JSImport] boundary was executed in `
            + `${version.Browser} and the frame loop was still ticking a second after the page `
            + 'finished booting.'
        );

        return 0;
    } finally {
        await cleanup();
    }
}

function transcript(consoleLines, pageErrors, failedRequests, misses) {
    const lines = ['\n── the page transcript ─────────────────────────────────────────'];
    for (const line of consoleLines) lines.push(`  [${line.type}] ${line.text}`);
    for (const error of pageErrors) lines.push(`  [threw] ${error}`);
    for (const failure of failedRequests) lines.push(`  [reqfail] ${failure}`);
    for (const miss of misses) lines.push(`  [404] ${miss}`);
    if (consoleLines.length === 0) lines.push('  (the page printed nothing at all)');
    return lines.join('\n');
}

try {
    process.exit(await main());
} catch (error) {
    console.error('\nBROWSER SMOKE FAILED\n');
    console.error(error instanceof SmokeError ? error.message : (error.stack ?? String(error)));
    process.exit(1);
}
