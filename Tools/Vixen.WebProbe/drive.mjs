// Drives a page in the cached headless-shell Chromium over CDP, prints every console message,
// and reports whether requestAnimationFrame actually fires.
// Usage: node drive.mjs <url> [millis] [screenshotPath]
import { chromium } from 'playwright-core';

const url = process.argv[2];
const millis = Number(process.argv[3] ?? 8000);
const shot = process.argv[4];

const browser = await chromium.connectOverCDP('http://127.0.0.1:9223');
const context = browser.contexts()[0] ?? await browser.newContext();
const page = await context.newPage();

page.on('console', message => console.log('[' + message.type() + '] ' + message.text()));
page.on('pageerror', error => console.log('[pageerror] ' + error.message));
page.on('requestfailed', request => console.log('[reqfail] ' + request.url() + ' ' + request.failure()?.errorText));

await page.goto(url, { waitUntil: 'load' });
await page.waitForTimeout(millis);

const rafCount = await page.evaluate(() => new Promise(resolve => {
    let n = 0;
    setTimeout(() => resolve(n), 1000);
    const step = () => { n++; requestAnimationFrame(step); };
    requestAnimationFrame(step);
}));
console.log('[probe] rAF fired ' + rafCount + ' times in 1s');

const text = await page.evaluate(() => document.getElementById('result')?.textContent ?? '(no #result)');
console.log('--- #result ---');
console.log(text);

if (shot) {
    await page.screenshot({ path: shot });
    console.log('[probe] screenshot -> ' + shot);
}

await page.close();
await browser.close();
