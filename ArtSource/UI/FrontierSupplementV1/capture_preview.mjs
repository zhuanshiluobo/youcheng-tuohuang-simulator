import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const directory = path.dirname(fileURLToPath(import.meta.url));
const targets = await (await fetch('http://127.0.0.1:9338/json/list')).json();
const target = targets.find(item => item.type === 'page');
if (!target) throw new Error('No review browser page');
const socket = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((resolve, reject) => {
  socket.addEventListener('open', resolve, { once: true });
  socket.addEventListener('error', reject, { once: true });
});
let sequence = 0;
const pending = new Map();
const errors = [];
socket.addEventListener('message', event => {
  const message = JSON.parse(event.data);
  if (message.method === 'Runtime.exceptionThrown') errors.push(message.params);
  if (!message.id) return;
  const callback = pending.get(message.id);
  pending.delete(message.id);
  if (message.error) callback.reject(new Error(JSON.stringify(message.error)));
  else callback.resolve(message.result);
});
function send(method, params = {}) {
  return new Promise((resolve, reject) => {
    const id = ++sequence;
    pending.set(id, { resolve, reject });
    socket.send(JSON.stringify({ id, method, params }));
  });
}
async function evaluate(expression) {
  const result = await send('Runtime.evaluate', {
    expression, awaitPromise: true, returnByValue: true,
  });
  if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
  return result.result.value;
}
async function viewport(width, height) {
  await send('Emulation.setDeviceMetricsOverride', {
    width, height, deviceScaleFactor: 1, mobile: false,
  });
  await evaluate('new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))');
}
async function screenshot(name) {
  const { cssContentSize } = await send('Page.getLayoutMetrics');
  const result = await send('Page.captureScreenshot', {
    format: 'png', captureBeyondViewport: true,
    clip: { x: 0, y: 0, width: cssContentSize.width, height: cssContentSize.height, scale: 1 },
  });
  await fs.writeFile(path.join(directory, name), Buffer.from(result.data, 'base64'));
  return { width: cssContentSize.width, height: cssContentSize.height };
}

const report = { browser: '本机 Chrome 无头预览', checks: {}, screenshots: {} };
try {
  await send('Page.enable');
  await send('Runtime.enable');
  await viewport(1440, 1200);
  await send('Page.navigate', { url: pathToFileURL(path.join(directory, 'preview.html')).href });
  for (let i = 0; i < 40; i++) {
    if (await evaluate('document.readyState === "complete" && Boolean(document.getElementById("icon-grid")?.children.length)')) break;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  await evaluate('document.fonts.ready.then(() => true)');
  report.checks.imagesLoaded = await evaluate('[...document.images].every(image => image.complete && image.naturalWidth > 0)');
  report.checks.projectFontLoaded = await evaluate('document.fonts.check("15px Project")');
  report.screenshots.desktop = await screenshot('preview-desktop.png');
  report.checks.checkbox = await evaluate(`(() => {
    const item = document.querySelector('.check:not(:checked):not(:disabled)');
    item.click(); return item.checked;
  })()`);
  report.checks.switch = await evaluate(`(() => {
    const item = document.querySelector('.switch:not(:disabled)');
    item.click(); return item.getAttribute('aria-checked') === 'false';
  })()`);
  report.checks.slider = await evaluate(`(() => {
    const item = document.getElementById('volume'); item.value = 83;
    item.dispatchEvent(new Event('input', {bubbles:true}));
    return document.getElementById('volume-value').textContent === '83%';
  })()`);
  report.checks.dropdown = await evaluate(`(() => {
    document.getElementById('dropdown-trigger').click();
    document.querySelectorAll('.option')[1].click();
    return document.getElementById('resolution-value').textContent === '1600 × 900' &&
      document.getElementById('resolution-menu').hidden;
  })()`);
  report.checks.invalidInput = await evaluate(`(() => {
    document.getElementById('room-code').value = 'A7';
    document.getElementById('validate-code').click();
    return document.getElementById('room-code').getAttribute('aria-invalid') === 'true';
  })()`);
  report.checks.validInput = await evaluate(`(() => {
    document.getElementById('room-code').value = 'A7K2P9';
    document.getElementById('validate-code').click();
    return document.getElementById('room-error').textContent === '' &&
      document.getElementById('toast-host').textContent.includes('编号格式正确');
  })()`);
  report.checks.offline = await evaluate(`(() => {
    document.getElementById('demo-offline').click();
    return document.getElementById('network-title').textContent === '连接已中断';
  })()`);
  report.checks.waiting = await evaluate(`(() => {
    document.getElementById('demo-retry').click();
    document.getElementById('toast-host').replaceChildren();
    return document.getElementById('demo-wait').disabled &&
      document.getElementById('network-icon').classList.contains('spinner');
  })()`);
  report.screenshots.waiting = await screenshot('preview-waiting.png');
  await new Promise(resolve => setTimeout(resolve, 2500));
  report.checks.waitCompletion = await evaluate('document.getElementById("network-title").textContent === "主机已确认"');
  await evaluate('document.getElementById("toast-host").replaceChildren(); document.getElementById("dropdown-trigger").click()');
  report.screenshots.menu = await screenshot('preview-menu.png');
  await evaluate('document.getElementById("dropdown-trigger").click()');
  await viewport(390, 844);
  report.checks.narrowNoHorizontalOverflow = await evaluate('document.documentElement.scrollWidth <= 390');
  report.screenshots.narrow = await screenshot('preview-narrow.png');
  report.checks.scriptErrors = errors.length === 0;
  report.failures = Object.entries(report.checks).filter(([, passed]) => !passed).map(([name]) => name);
  await fs.writeFile(path.join(directory, 'browser-review.json'), JSON.stringify(report, null, 2) + '\n');
  console.log(JSON.stringify(report));
} finally {
  await send('Browser.close').catch(() => {});
  socket.close();
}
if (report.failures?.length) process.exitCode = 1;
