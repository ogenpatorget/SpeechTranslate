let context, player, compressor, volumeNode, socket, stopping = false;

export async function startPlayback(url, callback, mode, volume) {
  await stopPlayback();
  stopping = false;
  context = new AudioContext();
  await context.audioWorklet.addModule(new URL("playback-worklet.js", document.baseURI));
  player = new AudioWorkletNode(context, "ct-playback");
  compressor = context.createDynamicsCompressor();
  compressor.threshold.value = -24;
  compressor.knee.value = 24;
  compressor.ratio.value = 2.5;
  compressor.attack.value = 0.01;
  compressor.release.value = 0.25;
  volumeNode = context.createGain();
  player.connect(compressor).connect(volumeNode).connect(context.destination);
  setPlaybackSettings(mode, volume);
  socket = new WebSocket(url);
  socket.binaryType = "arraybuffer";
  socket.onmessage = event => {
    const bytes = event.data;
    const header = new DataView(bytes);
    if (bytes.byteLength < 24 || header.getUint32(0, false) !== 0x43543031 ||
        header.getInt32(20, true) !== 24000) return;
    player?.port.postMessage({ pcm: bytes.slice(24) });
  };
  await new Promise((resolve, reject) => {
    socket.onopen = resolve;
    socket.onerror = () => reject(new Error("Could not connect to the live audio stream."));
  });
  socket.onclose = () => {
    if (!stopping) callback.invokeMethodAsync("PlaybackEnded");
    stopPlayback();
  };
  await context.resume();
}

export function setPlaybackSettings(mode, volume) {
  player?.port.postMessage({ mode });
  if (volumeNode) volumeNode.gain.setTargetAtTime(
    Math.max(0, Math.min(2, Number(volume) / 100)), context.currentTime, 0.02);
}

export async function stopPlayback() {
  stopping = true;
  if (socket && socket.readyState === WebSocket.OPEN) socket.close();
  socket = null;
  player?.port.postMessage({ reset: true });
  player?.disconnect();
  player = null;
  compressor?.disconnect();
  compressor = null;
  volumeNode?.disconnect();
  volumeNode = null;
  if (context && context.state !== "closed") await context.close();
  context = null;
}
