const modes = {
  fast: { targetMs: 80, maxWaitMs: 150 },
  balanced: { targetMs: 250, maxWaitMs: 350 },
  smooth: { targetMs: 500, maxWaitMs: 650 }
};

class PlaybackProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this.queue = [];
    this.offset = 0;
    this.position = 0;
    this.bufferedSamples = 0;
    this.waitedFrames = 0;
    this.playing = false;
    this.fadeIn = 0;
    this.lastSample = 0;
    this.mode = modes.smooth;
    this.port.onmessage = event => {
      if (event.data.reset) {
        this.queue = [];
        this.offset = this.position = this.bufferedSamples = this.waitedFrames = 0;
        this.playing = false;
        this.fadeIn = this.lastSample = 0;
        return;
      }
      if (event.data.mode) this.mode = modes[event.data.mode] ?? modes.balanced;
      if (!event.data.pcm) return;
      const chunk = new Int16Array(event.data.pcm);
      if (!chunk.length) return;
      this.queue.push(chunk);
      this.bufferedSamples += chunk.length;
      // Keep a stalled tab from accumulating an ever-increasing delay.
      while (this.queue.length > 1 && this.bufferedSamples > 48000) {
        this.bufferedSamples -= this.queue.shift().length - this.offset;
        this.offset = this.position = 0;
        this.playing = false;
      }
    };
  }

  nextValue() {
    const chunk = this.queue[0];
    if (!chunk) return null;
    const current = chunk[this.offset] / 32768;
    const following = this.offset + 1 < chunk.length ? chunk[this.offset + 1] / 32768 :
      (this.queue[1]?.[0] ?? chunk[this.offset]) / 32768;
    const value = current + (following - current) * this.position;
    this.position += 24000 / sampleRate;
    while (this.position >= 1) {
      this.position -= 1;
      this.offset++;
      this.bufferedSamples--;
      if (this.offset >= this.queue[0].length) {
        this.queue.shift();
        this.offset = 0;
        if (!this.queue.length) break;
      }
    }
    return value;
  }

  process(_inputs, outputs) {
    const output = outputs[0][0];
    if (!this.playing && this.bufferedSamples > 0) {
      this.waitedFrames += output.length;
      if (this.waitedFrames >= this.mode.targetMs * sampleRate / 1000 &&
          (this.bufferedSamples >= this.mode.targetMs * 24 ||
           this.waitedFrames >= this.mode.maxWaitMs * sampleRate / 1000)) {
        this.playing = true;
        this.waitedFrames = 0;
        this.fadeIn = 0;
      }
    }
    for (let i = 0; i < output.length; i++) {
      if (!this.playing) {
        this.lastSample *= 0.97;
        output[i] = this.lastSample;
        continue;
      }
      const value = this.nextValue();
      if (value === null) {
        this.playing = false;
        this.waitedFrames = 0;
        this.lastSample *= 0.97;
        output[i] = this.lastSample;
      } else {
        this.fadeIn = Math.min(this.fadeIn + 1, sampleRate * 0.005);
        this.lastSample = value * this.fadeIn / (sampleRate * 0.005);
        output[i] = this.lastSample;
      }
    }
    return true;
  }
}
registerProcessor("ct-playback", PlaybackProcessor);
