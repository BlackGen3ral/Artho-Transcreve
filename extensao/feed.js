let playingVideo;
let playingSource;

function boundary() {
  chrome.runtime.sendMessage({ target: 'worker', command: 'media-boundary' }).catch(() => {});
}

document.addEventListener('playing', ({ target }) => {
  if (!(target instanceof HTMLVideoElement)) return;
  if (playingVideo && (target !== playingVideo || playingSource !== target.currentSrc)) boundary();
  playingVideo = target;
  playingSource = target.currentSrc;
}, true);

for (const event of ['pause', 'emptied', 'seeking']) {
  document.addEventListener(event, ({ target }) => {
    if (target === playingVideo || (!playingVideo && target instanceof HTMLVideoElement)) boundary();
  }, true);
}
