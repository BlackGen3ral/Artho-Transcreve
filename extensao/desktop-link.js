const LOCAL_APP = 'http://127.0.0.1:48721';
let linkTask;

function listenForDesktop() {
  if (!linkTask) {
    linkTask = desktopLoop().catch(() => {}).finally(() => { linkTask = undefined; });
  }
}

async function desktopLoop() {
  let failures = 0;
  let lastCode;
  while (failures < 6) {
    const { pairingCode } = await chrome.storage.local.get('pairingCode');
    if (!pairingCode) return;
    if (pairingCode !== lastCode) {
      lastCode = pairingCode;
      failures = 0;
    }
    try {
      const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
      const command = await desktopPost('/control', pairingCode, { hasTikTokTab: isTikTok(tab?.url) });
      failures = 0;
      if (command?.request) {
        let result;
        try { result = await start(pairingCode, true); }
        catch (error) { result = { error: error.message }; }
        await desktopPost('/control-result', pairingCode, {
          id: command.request,
          error: result.state === 'active' ? '' : result.error || 'A extensão está ocupada. Tente novamente.',
        });
      }
    } catch {
      failures++;
      await new Promise(resolve => setTimeout(resolve, 2000));
    }
  }
}

async function desktopPost(path, code, data) {
  const headers = { 'X-Artho-Code': code };
  if (data !== undefined) headers['Content-Type'] = 'application/json';
  const response = await fetch(LOCAL_APP + path, {
    method: 'POST', headers, body: data === undefined ? undefined : JSON.stringify(data),
    signal: AbortSignal.timeout(path === '/control' ? 14000 : 2500), redirect: 'error',
  });
  if (response.status === 401) throw new Error('Código incorreto. Copie o código no app do Windows.');
  if (!response.ok) throw new Error('Abra a versão atual do Artho Transcreve no Windows.');
  return response.status === 204 ? null : response.json();
}

async function pairDesktop(code) {
  if (!/^[a-f0-9]{32}$/i.test(code || '')) {
    return { error: 'Copie o código no app do Windows e cole aqui.' };
  }
  try {
    const reply = await desktopPost('/version', code);
    if (!reply?.desktopControl) return { error: 'Atualize o app do Windows para iniciar a captura pelo PC.' };
    await chrome.storage.local.set({ pairingCode: code });
    listenForDesktop();
    return { state: 'idle', paired: true };
  } catch (error) {
    return { error: error.message === 'Failed to fetch' ? 'Abra o Artho Transcreve no Windows e tente novamente.' : error.message };
  }
}
