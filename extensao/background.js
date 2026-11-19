const OFFSCREEN = 'offscreen.html';
let creating;
let starting = false;

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
  if (starting) return { state: 'busy' };
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
    case 'start': return start(message.code);
    case 'status': return await hasOffscreen() ? sendCapture('status') : { state: 'idle' };
    case 'stop': return await hasOffscreen() ? sendCapture('stop') : { state: 'idle' };
    case 'badge': await updateBadge(message.active); return {};
    default: return { error: 'Comando desconhecido.' };
  }
}

chrome.runtime.onMessage.addListener((message, sender, respond) => {
  if (message.target !== 'worker' || sender.id !== chrome.runtime.id) return;
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
