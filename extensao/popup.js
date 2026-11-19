const form = document.querySelector('form');
const code = document.querySelector('#code');
const start = document.querySelector('#start');
const stop = document.querySelector('#stop');
const status = document.querySelector('#status');
let pending = false;

const ask = (command, payload) => chrome.runtime.sendMessage({ target: 'worker', command, ...payload });

function show(result = {}) {
  const state = result.state || 'idle';
  const active = state === 'active';
  const idle = state === 'idle';
  start.hidden = !idle;
  start.disabled = pending;
  stop.hidden = idle;
  stop.disabled = pending || !active;
  code.disabled = pending || !idle;
  stop.textContent = state === 'stopping' ? 'Encerrando…' : state === 'busy' ? 'Conectando…' : 'Parar transcrição';
  status.textContent = result.error || (active ? 'Capturando. Acompanhe o texto no app.'
    : idle ? 'Abra o TikTok, dê play e inicie a transcrição.' : '');
  status.toggleAttribute('data-error', Boolean(result.error));
}

async function act(command, payload) {
  pending = true;
  start.disabled = stop.disabled = code.disabled = true;
  status.textContent = command === 'start' ? 'Conectando ao app…' : 'Encerrando…';
  let result;
  try { result = await ask(command, payload); }
  catch (error) { result = { error: error.message }; }
  pending = false;
  show(result);
}

form.addEventListener('submit', event => {
  event.preventDefault();
  void act('start', { code: code.value.trim() });
});
stop.addEventListener('click', () => { void act('stop'); });
chrome.storage.local.get('pairingCode').then(({ pairingCode }) => { code.value = pairingCode || ''; });
ask('status').then(show).catch(error => show({ error: error.message }));
setInterval(async () => {
  if (!pending && !stop.hidden) {
    const result = await ask('status').catch(error => ({ error: error.message }));
    if (!pending) show(result);
  }
}, 1000);
