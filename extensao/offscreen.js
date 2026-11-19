const origin = 'http://127.0.0.1:48721';
const MAX_QUEUE = 5;
let current;
let recentError = '';

function status() {
  const state = current?.state || 'idle';
  const level = state === 'active' && Date.now() - current.meterAt < 1400 ? current.level : 0;
  return { state, level, pending: current?.pending || 0, error: recentError };
}

function badge(active) {
  chrome.runtime.sendMessage({ target: 'worker', command: 'badge', active }).catch(() => {});
}

async function post(session, route, { audio, offset } = {}) {
  const headers = { 'X-Artho-Code': session.code };
  if (session.id) headers['X-Artho-Session'] = session.id;
  if (route === '/start') headers['X-Artho-Capture'] = '2';
  if (route === '/ping') headers['X-Artho-Level'] = String(status().level);
  if (audio) {
    headers['X-Artho-Offset'] = String(offset);
    headers['Content-Type'] = 'application/octet-stream';
  }
  let response;
  try {
    response = await fetch(origin + route, {
      method: 'POST', headers, body: audio,
      signal: AbortSignal.timeout(audio ? 65000 : 4500),
    });
  } catch {
    throw new Error(audio ? 'O app deixou de responder. Reabra e inicie uma nova captura.'
      : 'Abra o app Artho Transcreve no PC e tente novamente.');
  }
  if (!response.ok) {
    const { error } = await response.json().catch(() => ({}));
    throw new Error(error || 'Falha ao conectar ao app.');
  }
  return route === '/start' || (route === '/ping' && response.status === 200) ? response.json() : undefined;
}

function enqueue(session, { audio, offset }) {
  if (current !== session || session.abort || !audio) return;
  if (session.pending >= MAX_QUEUE) {
    void stop(session, 'O PC não acompanhou a reprodução. A captura foi encerrada.', false);
    return;
  }
  session.pending++;
  session.queue = session.queue.then(async () => {
    if (!session.abort) await post(session, '/audio', { audio, offset });
  }).catch(error => {
    void stop(session, error.message, false);
  }).finally(() => { session.pending--; });
}

function disconnect(session) {
  for (const track of session.stream?.getTracks() || []) track.stop();
  if (session.context && session.context.state !== 'closed') {
    void session.context.close().catch(() => {});
  }
}

async function stop(session, error = '', flush = true) {
  if (!session || current !== session) return status();
  if (error) recentError = error;
  if (!flush) session.abort = true;
  if (session.state === 'stopping') return status();
  const wasActive = session.state === 'active';
  session.state = 'stopping';
  if (flush && wasActive && session.node) {
    // Stop recording immediately. Drain the last accepted samples before closing the session.
    const last = new Promise(resolve => { session.onFlush = resolve; });
    session.node.port.postMessage('flush');
    await Promise.race([last, new Promise(resolve => setTimeout(resolve, 350))]);
  }
  disconnect(session);
  if (flush) await session.queue;
  clearInterval(session.heartbeat);
  if (session.id) await post(session, '/stop').catch(() => {});
  if (current === session) current = undefined;
  badge(false);
  return status();
}

async function start({ streamId, code, tabId }) {
  if (current) return status();
  recentError = '';
  const session = { tabId, code, state: 'busy', pending: 0, queue: Promise.resolve(), level: 0, meterAt: 0 };
  current = session;
  try {
    const context = new AudioContext();
    session.context = context;
    await context.audioWorklet.addModule('pcm-worklet.js');
    if (current !== session || session.state !== 'busy') return status();
    const result = await post(session, '/start');
    session.id = result.session;
    if (current !== session || session.state !== 'busy') {
      await post(session, '/stop').catch(() => {});
      disconnect(session);
      return status();
    }

    const stream = await navigator.mediaDevices.getUserMedia({
      audio: { mandatory: { chromeMediaSource: 'tab', chromeMediaSourceId: streamId } },
      video: false,
    });
    session.stream = stream;
    if (current !== session || session.state !== 'busy') {
      disconnect(session);
      return status();
    }
    for (const track of stream.getTracks()) track.onended = () => { void stop(session); };

    const source = context.createMediaStreamSource(stream);
    // Keep the listening path at the device rate and in stereo. Only the worklet branch is resampled.
    source.connect(context.destination);
    const node = new AudioWorkletNode(context, 'artho-pcm', {
      channelCount: 1, channelCountMode: 'explicit', numberOfOutputs: 1,
      processorOptions: { chunkSeconds: result.chunkSeconds },
    });
    session.node = node;
    node.port.onmessage = ({ data }) => {
      if (Number.isInteger(data.level)) {
        session.level = data.level;
        session.meterAt = Date.now();
        return;
      }
      enqueue(session, data);
      if (data.flushed) session.onFlush?.();
    };
    node.onprocessorerror = () => {
      void stop(session, 'Falha na captura de áudio da aba. Inicie novamente.', false);
    };
    source.connect(node);
    const silent = context.createGain();
    silent.gain.value = 0;
    node.connect(silent).connect(context.destination);
    await Promise.race([context.resume(), new Promise((_, reject) => {
      setTimeout(() => reject(new Error('O navegador bloqueou a reprodução da aba. Dê play no vídeo e tente novamente.')), 4500);
    })]);
    if (current !== session || session.state !== 'busy') return status();
    session.state = 'active';
    session.heartbeat = setInterval(() => { void heartbeat(session); }, 1000);
    badge(true);
    return status();
  } catch (error) {
    return stop(session, error.message, false);
  }
}

async function heartbeat(session) {
  if (session.abort || session.pinging) return;
  session.pinging = true;
  try {
    const result = await post(session, '/ping');
    if (result?.finish && session.state === 'active') void stop(session);
  } catch (error) { void stop(session, error.message, false); }
  finally { session.pinging = false; }
}

async function route(message) {
  switch (message.command) {
    case 'start': return start(message);
    case 'status': return status();
    case 'stop': return stop(current);
    case 'stop-tab': return current?.tabId === message.tabId ? stop(current) : status();
    case 'split-tab':
      if (current?.tabId === message.tabId && current.state === 'active') current.node.port.postMessage('split');
      return status();
    default: return status();
  }
}

chrome.runtime.onMessage.addListener((message, sender, respond) => {
  if (message.target !== 'capture' || sender.id !== chrome.runtime.id) return;
  route(message).then(respond, error => respond({ state: 'idle', error: error.message }));
  return true;
});
