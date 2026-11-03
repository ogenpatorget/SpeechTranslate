class CaptureProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this.position = 0;
    this.samples = new Int16Array(4800);
    this.count = 0;
    this.frames = 0;
  }
  process(inputs) {
    const input = inputs[0]?.[0];
    if (!input) return true;
    let peak = 0;
    for (const value of input) {
      peak = Math.max(peak, Math.abs(value));
      this.position += 24000 / sampleRate;
      if (this.position < 1) continue;
      this.position -= 1;
      this.samples[this.count++] = Math.round(Math.max(-1, Math.min(1, value)) * 32767);
      if (this.count === 4800) {
        this.port.postMessage({ pcm: this.samples.buffer }, [this.samples.buffer]);
        this.samples = new Int16Array(4800);
        this.count = 0;
      }
    }
    if (++this.frames % 10 === 0) this.port.postMessage({ level: Math.min(100, peak * 180) });
    return true;
  }
}
registerProcessor("ct-capture", CaptureProcessor);
