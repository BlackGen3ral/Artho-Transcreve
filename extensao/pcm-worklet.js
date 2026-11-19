const PCM_RATE = 16000;
const FRAME_SAMPLES = PCM_RATE / 100;
const PHASES = 64;
const TAPS = 64;

// Windowed low-pass interpolation keeps ultrasonic and out-of-band audio from
// folding into the speech track when reducing the device rate to 16 kHz.
function coefficients(inputRate) {
  const filter = new Float32Array(PHASES * TAPS);
  const cutoff = 0.45 * Math.min(1, PCM_RATE / inputRate);
  for (let phase = 0; phase < PHASES; phase++) {
    let sum = 0;
    for (let tap = 0; tap < TAPS; tap++) {
      const distance = tap - TAPS / 2 + 1 - phase / PHASES;
      const argument = 2 * Math.PI * cutoff * distance;
      const sinc = argument === 0 ? 1 : Math.sin(argument) / argument;
      const window = 0.5 + 0.5 * Math.cos(2 * Math.PI * distance / TAPS);
      const weight = 2 * cutoff * sinc * window;
      filter[phase * TAPS + tap] = weight;
      sum += weight;
    }
    for (let tap = 0; tap < TAPS; tap++) filter[phase * TAPS + tap] /= sum;
  }
  return filter;
}

class ArthoPcm extends AudioWorkletProcessor {
  constructor(options) {
    super();
    const seconds = options.processorOptions?.chunkSeconds === 10 ? 10 : 4;
    this.samples = new ArrayBuffer(PCM_RATE * seconds * 2);
    this.view = new DataView(this.samples);
    this.minimum = PCM_RATE * Math.min(5, seconds);
    this.position = 0;
    this.offset = 0;
    this.frameEnergy = 0;
    this.loudFrames = 0;
    this.quietFrames = 0;
    this.recording = true;
    this.filter = sampleRate === PCM_RATE ? null : coefficients(sampleRate);
    this.ring = new Float32Array(TAPS * 2);
    this.inputIndex = 0;
    this.nextOutput = 0;
    this.port.onmessage = ({ data }) => {
      if (data === 'flush') {
        this.recording = false;
        this.send(true);
      }
    };
  }

  send(flushed = false) {
    if (this.position >= PCM_RATE / 5 && this.loudFrames >= 3) {
      const audio = this.samples.slice(0, this.position * 2);
      this.port.postMessage({ audio, offset: this.offset / PCM_RATE, flushed }, [audio]);
    } else if (flushed) {
      this.port.postMessage({ flushed: true });
    }
    this.offset += this.position;
    this.position = 0;
    this.frameEnergy = 0;
    this.loudFrames = 0;
    this.quietFrames = 0;
  }

  append(sample) {
    const clipped = Math.min(1, Math.max(-1, sample));
    this.frameEnergy += clipped * clipped;
    this.view.setInt16(this.position * 2, clipped * (clipped < 0 ? 32768 : 32767), true);
    this.position++;
    if (this.position % FRAME_SAMPLES === 0) {
      if (this.frameEnergy / FRAME_SAMPLES >= 0.000009) {
        this.loudFrames++;
        this.quietFrames = 0;
      } else {
        this.quietFrames++;
      }
      this.frameEnergy = 0;
    }
    if (this.position * 2 === this.samples.byteLength ||
        (this.position >= this.minimum && this.quietFrames >= 45)) this.send();
  }

  process(inputs) {
    if (!this.recording) return false;
    const input = inputs[0][0];
    if (!input) return true;
    for (const sample of input) {
      if (!this.filter) {
        this.append(sample);
        continue;
      }
      const mask = this.ring.length - 1;
      this.ring[this.inputIndex & mask] = sample;
      let center = Math.floor(this.nextOutput / PCM_RATE);
      while (center + TAPS / 2 <= this.inputIndex) {
        const phase = Math.floor((this.nextOutput - center * PCM_RATE) * PHASES / PCM_RATE);
        const start = center - TAPS / 2 + 1;
        let filtered = 0;
        for (let tap = 0; tap < TAPS; tap++) {
          filtered += this.ring[(start + tap) & mask] * this.filter[phase * TAPS + tap];
        }
        this.append(filtered);
        this.nextOutput += sampleRate;
        center = Math.floor(this.nextOutput / PCM_RATE);
      }
      this.inputIndex++;
    }
    return true;
  }
}
registerProcessor('artho-pcm', ArthoPcm);
