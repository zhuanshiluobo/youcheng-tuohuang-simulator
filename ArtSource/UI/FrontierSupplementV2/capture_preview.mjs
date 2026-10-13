import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const directory = path.dirname(fileURLToPath(import.meta.url));
const revisionOnly = process.argv.includes('--revision');
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
  const result = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
  return result.result.value;
}
async function viewport(width, height) {
  await send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });
  await evaluate('new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))');
}
async function screenshot(name, heightLimit) {
  const { cssContentSize } = await send('Page.getLayoutMetrics');
  const height = heightLimit || cssContentSize.height;
  const result = await send('Page.captureScreenshot', {
    format: 'png', captureBeyondViewport: true,
    clip: { x: 0, y: 0, width: cssContentSize.width, height, scale: 1 },
  });
  await fs.writeFile(path.join(directory, name), Buffer.from(result.data, 'base64'));
  return { width: cssContentSize.width, height };
}

async function revisionReview(report) {
  await viewport(1600, 1100);
  await send('Page.navigate', { url: pathToFileURL(path.join(directory, 'revision.html')).href });
  for (let i=0; i<40; i++) {
    if (await evaluate('document.readyState === "complete" && Boolean(document.querySelector(".check-shell"))')) break;
    await new Promise(resolve => setTimeout(resolve,100));
  }
  await evaluate('document.fonts.ready.then(() => Promise.all([...document.images].map(image => image.decode())))');
  report.checks.referenceImage = await evaluate('[...document.images].every(image => image.complete && image.naturalWidth>0)');
  report.checks.initialCheckboxStates = await evaluate(`(() => { const inputs=[...document.querySelectorAll('.check-examples .check')]; return !inputs[0].checked && inputs[1].checked && inputs[2].checked && inputs[2].disabled; })()`);
  report.screenshots.revision = await screenshot('revision-approval.png');
  report.checks.directCheckboxClick = await evaluate(`(() => {
    const input=document.querySelector('.check-examples .check'),label=input.closest('label').querySelector('.check-state-label');
    input.click(); const selected=input.checked && label.textContent==='选中';
    input.click(); return selected && !input.checked && label.textContent==='未选中';
  })()`);
  report.checkboxSelect = await evaluate(`(async () => {
    document.getElementById('select-check').click();
    await new Promise(resolve => setTimeout(resolve,60));
    const input=document.getElementById('motion-check'),mark=input.nextElementSibling.querySelector('.check-mark');
    return { checked:input.checked,opacity:Number(getComputedStyle(mark).opacity),label:input.closest('label').querySelector('.check-state-label').textContent };
  })()`);
  report.checks.selectAnimation = report.checkboxSelect.checked && report.checkboxSelect.opacity>0 && report.checkboxSelect.opacity<1 && report.checkboxSelect.label==='选中';
  await new Promise(resolve=>setTimeout(resolve,180));
  report.checkboxCancel = await evaluate(`(async () => {
    document.getElementById('cancel-check').click();
    await new Promise(resolve => setTimeout(resolve,50));
    const input=document.getElementById('motion-check'),mark=input.nextElementSibling.querySelector('.check-mark');
    return { checked:input.checked,opacity:Number(getComputedStyle(mark).opacity),label:input.closest('label').querySelector('.check-state-label').textContent };
  })()`);
  report.checks.cancelAnimation = !report.checkboxCancel.checked && report.checkboxCancel.opacity>0 && report.checkboxCancel.opacity<1 && report.checkboxCancel.label==='未选中';
  await new Promise(resolve=>setTimeout(resolve,160));
  report.checks.disabledCheckbox = await evaluate(`(() => { const input=document.querySelector('.check-examples .check:disabled'); input.click(); return input.checked && getComputedStyle(input.nextElementSibling.querySelector('.check-mark')).display==='none'; })()`);
  report.slotGeometry = await evaluate(`[...document.querySelectorAll('.message-slot')].map(item=>({width:item.getBoundingClientRect().width,height:item.getBoundingClientRect().height}))`);
  report.checks.compactSlotSize = report.slotGeometry.every(slot => slot.width===1424 && slot.height===60);
  report.checks.slotTextFits = await evaluate(`[...document.querySelectorAll('.message-content span')].every(item=>item.getBoundingClientRect().height<=item.parentElement.getBoundingClientRect().height)`);
  await viewport(390,844);
  report.checks.narrowNoHorizontalOverflow = await evaluate('document.documentElement.scrollWidth<=390');
  report.screenshots.narrow = await screenshot('revision-narrow.png');
}

const report = { browser: '本机 Chrome 无头预览', checks: {}, screenshots: {}, unityIntegration: '未执行，等待审批' };
try {
  await send('Page.enable');
  await send('Runtime.enable');
  if (revisionOnly) {
    await revisionReview(report);
  } else {
  await viewport(1440, 1100);
  await send('Page.navigate', { url: pathToFileURL(path.join(directory, 'preview.html')).href });
  for (let i = 0; i < 40; i++) {
    if (await evaluate('document.readyState === "complete" && Boolean(document.getElementById("icon-grid")?.children.length)')) break;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  await evaluate('document.fonts.ready.then(() => true)');
  await evaluate('Promise.all([...document.images].map(image => image.decode().catch(() => false)))');
  report.checks.imagesLoaded = await evaluate('[...document.images].every(image => image.complete && image.naturalWidth > 0)');
  report.missingImages = await evaluate('[...document.images].filter(image => !image.complete || !image.naturalWidth).map(image => image.src)');
  report.checks.projectFonts = await evaluate('document.fonts.check("15px Project") && document.fonts.check("24px Heading")');
  report.screenshots.approval = await screenshot('approval.png', 1100);
  report.screenshots.desktop = await screenshot('preview-desktop.png');
  report.checks.checkbox = await evaluate(`(() => { const item = document.querySelector('.check:not(:checked):not(:disabled)'); item.click(); return item.checked; })()`);
  report.checks.switch = await evaluate(`(() => { const item = document.querySelector('.switch:not(:disabled)'); item.click(); return item.getAttribute('aria-checked') === 'false'; })()`);
  report.checks.slider = await evaluate(`(() => { const item = document.getElementById('volume'); item.value = 83; item.dispatchEvent(new Event('input', {bubbles:true})); return document.getElementById('volume-value').textContent === '83%'; })()`);
  await evaluate('document.getElementById("dropdown-trigger").click()');
  report.screenshots.menu = await screenshot('preview-menu.png', 840);
  report.checks.dropdown = await evaluate(`(() => { document.querySelector('.option').click(); return document.getElementById('map-value').textContent === '三人拓荒区' && document.getElementById('map-menu').hidden; })()`);
  report.checks.invalidInput = await evaluate(`(() => { document.getElementById('room-code').value = 'A7'; document.getElementById('validate-code').click(); return document.getElementById('room-code').getAttribute('aria-invalid') === 'true'; })()`);
  report.checks.validInput = await evaluate(`(() => { document.getElementById('room-code').value = 'A7K2P9'; document.getElementById('validate-code').click(); return document.getElementById('room-error').textContent === '' && document.getElementById('toast-host').textContent.includes('编号格式正确'); })()`);
  report.checks.closeAndRestore = await evaluate(`(() => { document.getElementById('close-window').click(); const closed = document.getElementById('dispatch').hidden; document.getElementById('restore-window').click(); return closed && !document.getElementById('dispatch').hidden; })()`);
  report.checks.offline = await evaluate(`(() => { document.getElementById('toast-host').replaceChildren(); document.getElementById('demo-offline').click(); return document.getElementById('network-title').textContent === '连接已中断'; })()`);
  report.screenshots.offline = await screenshot('preview-offline.png', 840);
  report.checks.waiting = await evaluate(`(() => { document.getElementById('demo-retry').click(); return document.getElementById('demo-wait').disabled && document.getElementById('network-icon').classList.contains('spinner'); })()`);
  await new Promise(resolve => setTimeout(resolve, 2200));
  report.checks.waitCompletion = await evaluate('document.getElementById("network-title").textContent === "主机已确认"');
  await evaluate('document.activeElement.blur()');
  await viewport(390, 844);
  report.checks.narrowNoHorizontalOverflow = await evaluate('document.documentElement.scrollWidth <= 390');
  report.checks.narrowWindowContainsContent = await evaluate(`(() => { const outer = document.getElementById('dispatch').getBoundingClientRect(); return [...document.querySelectorAll('.window-body button,.window-body input,.window-body .notice')].filter(item => item.getClientRects().length).every(item => { const r=item.getBoundingClientRect(); return r.left>=outer.left && r.right<=outer.right && r.bottom<=outer.bottom; }); })()`);
  report.screenshots.narrow = await screenshot('preview-narrow.png');
  }
  report.checks.scriptErrors = errors.length === 0;
  report.failures = Object.entries(report.checks).filter(([, passed]) => !passed).map(([name]) => name);
  await fs.writeFile(path.join(directory, revisionOnly ? 'revision-review.json' : 'browser-review.json'), JSON.stringify(report, null, 2) + '\n');
  console.log(JSON.stringify(report));
} finally {
  await send('Browser.close').catch(() => {});
  socket.close();
}
if (report.failures?.length) process.exitCode = 1;
