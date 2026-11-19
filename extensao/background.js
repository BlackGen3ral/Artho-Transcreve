const OFFSCREEN = 'offscreen.html';
const UPDATE_ALARM = 'artho-extension-update';
let creating;
let starting = false;
let checkingUpdate;
let reloading = false;

async function hasOffscreen() {
  const contexts = await chrome.runtime.getContexts({
    contextTypes: ['OFFSCREEN_DOCUMENT'],
    documentUrls: [chrome.runtime.getURL(OFFSCREEN)],
  });
  return contexts.length > 0;
}

async function ensureOffscreen() {
  if (await hasOffscreen()) return;
  if (!creating) {
    creating = chrome.offscreen.createDocument({
      url: OFFSCREEN,
      reasons: ['USER_MEDIA', 'AUDIO_PLAYBACK'],
      justification: 'Captura iniciada pelo usuário e reprodução contínua do áudio da aba TikTok.',
    }).finally(() => { creating = undefined; });
  }
  await creating;
}

function isTikTok(value) {
  try {
    const url = new URL(value);
    return url.protocol === 'https:' &&
      (url.hostname === 'tiktok.com' || url.hostname.endsWith('.tiktok.com'));
  } catch { return false; }
}

function sendCapture(command, details = {}) {
  return chrome.runtime.sendMessage({ target: 'capture', command, ...details });
}

async function updateBadge(active) {
  await chrome.action.setBadgeBackgroundColor({ color: '#2459bd' });
  await chrome.action.setBadgeText({ text: active ? 'REC' : '' });
}

async function start(code) {
  if (starting || reloading) return { state: 'busy' };
  if (!/^[a-f0-9]{32}$/i.test(code || '')) {
    return { error: 'Cole o código de conexão do app Artho Transcreve.' };
  }
  starting = true;
  try {
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    if (!isTikTok(tab?.url)) return { error: 'Abra o TikTok e inicie a captura nessa aba.' };
    await ensureOffscreen();
    const current = await sendCapture('status');
    if (current.state !== 'idle') return current;

    const streamId = await chrome.tabCapture.getMediaStreamId({ targetTabId: tab.id });
    const result = await sendCapture('start', { streamId, code, tabId: tab.id });
    if (result.state === 'active') {
      await chrome.storage.local.set({ pairingCode: code });
      const latest = await chrome.tabs.get(tab.id).catch(() => null);
      if (!isTikTok(latest?.url)) return await sendCapture('stop');
    }
    return result;
  } finally { starting = false; }
}

async function route(message) {
  switch (message.command) {
    case 'media-boundary':
      return await hasOffscreen() ? sendCapture('split-tab', { tabId: message.tabId }) : {};
    case 'start': return start(message.code);
    case 'status': {
      const current = await captureStatus();
      if (current.state === 'idle') {
        const error = await checkExtensionUpdate();
        if (error && !current.error) return { ...current, error };
      }
      return current;
    }
    case 'stop': return await hasOffscreen() ? sendCapture('stop') : { state: 'idle' };
    case 'badge': await updateBadge(message.active); return {};
    default: return { error: 'Comando desconhecido.' };
  }
}

async function captureStatus() {
  return await hasOffscreen() ? sendCapture('status') : { state: 'idle' };
}

function checkExtensionUpdate() {
  if (!checkingUpdate) {
    checkingUpdate = refreshExtension().catch(() => undefined).finally(() => { checkingUpdate = undefined; });
  }
  return checkingUpdate;
}

async function refreshExtension() {
  if (starting || reloading) return;
  const { pairingCode, lastReloadVersion } = await chrome.storage.local.get(['pairingCode', 'lastReloadVersion']);
  if (!pairingCode) return;
  const response = await fetch('http://127.0.0.1:48721/version', {
    method: 'POST', headers: { 'X-Artho-Code': pairingCode },
    signal: AbortSignal.timeout(1500), redirect: 'error',
  });
  if (!response.ok) return;
  const { extensionVersion } = await response.json();
  if (!/^\d+\.\d+\.\d+$/.test(extensionVersion || '')) return;
  const current = chrome.runtime.getManifest().version.split('.').map(Number);
  const difference = extensionVersion.split('.').map((part, index) => Number(part) - (current[index] || 0));
  if ((difference.find(part => part !== 0) || 0) <= 0) return;
  if (lastReloadVersion === extensionVersion) {
    return 'Recarregue a extensão em Gerenciar extensões. Use a pasta extensao ao lado do app.';
  }
  if (starting || (await captureStatus()).state !== 'idle' || starting) return;
  reloading = true;
  try {
    await chrome.storage.local.set({ lastReloadVersion: extensionVersion });
    chrome.runtime.reload();
  } finally { reloading = false; }
}

async function scheduleUpdateChecks() {
  if (!await chrome.alarms.get(UPDATE_ALARM)) {
    await chrome.alarms.create(UPDATE_ALARM, { delayInMinutes: 1, periodInMinutes: 1 });
  }
}

chrome.runtime.onStartup.addListener(() => { void checkExtensionUpdate(); });
chrome.alarms.onAlarm.addListener(alarm => {
  if (alarm.name === UPDATE_ALARM) void checkExtensionUpdate();
});
void scheduleUpdateChecks().catch(() => {});

chrome.runtime.onMessage.addListener((message, sender, respond) => {
  if (message.target !== 'worker' || sender.id !== chrome.runtime.id) return;
  if (message.command === 'media-boundary') {
    if (sender.frameId !== 0 || !Number.isInteger(sender.tab?.id) || !isTikTok(sender.url)) return;
    message = { ...message, tabId: sender.tab.id };
  }
  route(message).then(respond, error => respond({ error: error.message }));
  return true;
});

async function stopIfCaptured(tabId) {
  if (await hasOffscreen()) await sendCapture('stop-tab', { tabId });
}
chrome.tabs.onRemoved.addListener(tabId => { stopIfCaptured(tabId).catch(() => {}); });
chrome.tabs.onUpdated.addListener((tabId, change) => {
  if (change.url && !isTikTok(change.url)) stopIfCaptured(tabId).catch(() => {});
});
chrome.tabCapture.onStatusChanged.addListener(({ tabId, status }) => {
  if (status === 'stopped' || status === 'error') stopIfCaptured(tabId).catch(() => {});
});
