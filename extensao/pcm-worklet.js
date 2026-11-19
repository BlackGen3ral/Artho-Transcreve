class ArthoPcm extends AudioWorkletProcessor {
  constructor() {
    super();
    this.samples = new ArrayBuffer(sampleRate * 4 * 2);
    this.view = new DataView(this.samples);
    this.position = 0;
    this.offset = 0;
    this.energy = 0;
    this.recording = true;
    this.port.onmessage = ({ data }) => {
      if (data === 'flush') {
        this.recording = false;
        this.send(true);
      }
    };
  }

  send(flushed = false) {
    const hasAudio = this.position >= sampleRate / 5 && this.energy / this.position >= 0.000009;
    if (hasAudio) {
      const audio = this.samples.slice(0, this.position * 2);
      this.port.postMessage({ audio, offset: this.offset / sampleRate, flushed }, [audio]);
    } else if (flushed) {
      this.port.postMessage({ flushed: true });
    }
    this.offset += this.position;
    this.position = 0;
    this.energy = 0;
  }

  process(inputs) {
    if (!this.recording) return false;
    const input = inputs[0][0];
    if (!input) return true;
    for (const sample of input) {
      const clipped = Math.min(1, Math.max(-1, sample));
      this.energy += clipped * clipped;
      this.view.setInt16(this.position * 2, clipped * (clipped < 0 ? 32768 : 32767), true);
      this.position++;
      if (this.position * 2 === this.samples.byteLength) this.send();
    }
    return true;
  }
}
registerProcessor('artho-pcm', ArthoPcm);
