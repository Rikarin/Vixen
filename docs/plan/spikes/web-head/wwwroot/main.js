const out = [];
const paint = () => {
    const el = document.getElementById('result');
    if (el) el.textContent = out.join('\n');
};
const record = (tag) => (...args) => { out.push(tag + ' ' + args.join(' ')); paint(); };
const nativeLog = console.log.bind(console);
console.log = (...a) => { record('LOG')(...a); nativeLog(...a); };
console.error = (...a) => { record('ERR')(...a); nativeLog(...a); };
window.addEventListener('error', e => { record('WINERR')(e.message); });
window.addEventListener('unhandledrejection', e => { record('REJECT')(e.reason && (e.reason.stack || e.reason.message || e.reason)); });

try {
    const { dotnet } = await import('./_framework/dotnet.js');
    // ⚠ runMain, not run. dotnet.run() exits the runtime when Main returns, which kills every
    // requestAnimationFrame callback WebFrameLoop registered. See the report.
    const runtime = await dotnet.create();
    await runtime.runMain();
    out.push('BOOT ok, runtime still alive');
} catch (e) {
    out.push('BOOT-THREW ' + (e && (e.stack || e.message || e)));
}
paint();
