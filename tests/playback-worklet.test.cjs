const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

let Processor;
const source = fs.readFileSync(path.join(__dirname, '../src/ChurchTranslate.Listener/wwwroot/playback-worklet.js'), 'utf8');
vm.runInNewContext(source, {
  AudioWorkletProcessor: class { constructor() { this.port = { onmessage: null }; } },
  sampleRate: 48000,
  Int16Array,
  registerProcessor: (_name, type) => { Processor = type; }
});

function render(processor, frames) {
  let peak = 0;
  for (let i = 0; i < frames; i++) {
    const output = new Float32Array(128);
    processor.process([], [[output]]);
    for (const sample of output) peak = Math.max(peak, Math.abs(sample));
  }
  return peak;
}

test('smooth mode waits for a reserve before starting and reset clears it', () => {
  const processor = new Processor();
  processor.port.onmessage({ data: { pcm: new Int16Array(24000).fill(12000).buffer } });
  assert.equal(render(processor, 187), 0);
  assert.ok(render(processor, 1) > 0);
  processor.port.onmessage({ data: { reset: true } });
  assert.equal(render(processor, 5), 0);
});

test('fast mode starts sooner than smooth mode', () => {
  const processor = new Processor();
  processor.port.onmessage({ data: { mode: 'fast' } });
  processor.port.onmessage({ data: { pcm: new Int16Array(4800).fill(12000).buffer } });
  assert.equal(render(processor, 29), 0);
  assert.ok(render(processor, 1) > 0);
});
