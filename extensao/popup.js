const form = document.querySelector('form');
const pair = document.querySelector('#pair');
const code = document.querySelector('#code');
const start = document.querySelector('#start');
const stop = document.querySelector('#stop');
const connect = document.querySelector('#connect');
const hint = document.querySelector('#hint');
const stateText = document.querySelector('#state');
const level = document.querySelector('#level');
const capture = document.querySelector('.capture');
let pending = false;
let loaded = false;
let state = 'idle';

const ask = (command, payload) => chrome.runtime.sendMessage({ target: 'worker', command, ...payload });

function show(result = {}) {
  state = result.state || 'idle';
  const active = state === 'active';
  const idle = state === 'idle';
  const hasSignal = active && result.level > 0;
  capture.toggleAttribute('data-active', active);
  start.hidden = !idle;
  start.disabled = pending || !loaded;
  stop.hidden = idle;
  stop.disabled = pending || !active;
  code.disabled = pending || !idle;
  connect.disabled = pending || !idle;
  stateText.textContent = active ? 'Capturando' : state === 'stopping' ? 'Finalizando' : state === 'busy' ? 'Conectando' : 'Aguardando';
  level.value = active ? (result.level || 0) : 0;
  stop.textContent = active ? 'Finalizar captura' : state === 'busy' ? 'Conectando…' : 'Finalizando…';
  hint.textContent = result.error || (active
    ? hasSignal ? 'Áudio recebido. Veja o texto no app do Windows.'
      : result.pending > 0 ? 'Reconhecendo o trecho recebido.'
      : 'Aguardando som. Confirme que o vídeo está tocando com áudio.'
    : state === 'stopping' ? 'Enviando os últimos trechos ao app.'
    : state === 'busy' ? 'Preparando a captura da aba.'
    : result.paired ? 'Conectado. Volte ao app do Windows e clique em Iniciar captura.'
    : 'Abra o TikTok e dê play para começar.');
  hint.toggleAttribute('data-error', Boolean(result.error));
}

async function act(command, payload) {
  pending = true;
  show({ state: command === 'stop' ? 'stopping' : 'busy' });
  let result;
  try { result = await ask(command, payload); }
  catch (error) { result = { error: error.message }; }
  pending = false;
  show(result);
  if (result.state === 'active' || result.paired) pair.open = false;
}

form.addEventListener('submit', event => {
  event.preventDefault();
  const pairingCode = code.value.trim();
  if (!/^[a-f0-9]{32}$/i.test(pairingCode)) {
    pair.open = true;
    show({ error: 'Copie o código no app do Windows e cole aqui.' });
    code.focus();
    return;
  }
  void act('start', { code: pairingCode });
});
stop.addEventListener('click', () => { void act('stop'); });
connect.addEventListener('click', () => { void act('pair', { code: code.value.trim() }); });

async function initialize() {
  const preferences = await chrome.storage.local.get('pairingCode').catch(() => ({}));
  code.value = preferences.pairingCode || '';
  pair.open = !code.value;
  loaded = true;
  const result = await ask('status').catch(error => ({ error: error.message }));
  if (!pending) show(result);
}
void initialize();

let refreshing = false;
setInterval(async () => {
  if (state === 'idle' || pending || refreshing) return;
  refreshing = true;
  try {
    const result = await ask('status').catch(error => ({ error: error.message }));
    if (!pending) show(result);
  } finally { refreshing = false; }
}, 350);
