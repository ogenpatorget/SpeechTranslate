let context, player, compressor, volumeNode, socket, stopping = false;

function usePlaybackAudioSession() {
  try {
    if (navigator.audioSession) navigator.audioSession.type = "playback";
  } catch { /* Older browsers can ignore this hint. */ }
}

// iOS requires the audio context to be unlocked during the tap itself. The
// broadcast token is fetched later, after the browser's user gesture expires.
document.addEventListener("click", event => {
  const button = event.target.closest?.(".play-button[data-audio-start='True'], .play-button[data-audio-start='true']");
  if (!button || button.disabled) return;
  usePlaybackAudioSession();
  if (!context || context.state === "closed") context = new AudioContext();
  void context.resume();
}, true);

export async function startPlayback(url, callback, mode, volume) {
  if (socket || player) await stopPlayback();
  stopping = false;
  usePlaybackAudioSession();
  context ??= new AudioContext();
  const playbackContext = context;
  playbackContext.onstatechange = () => {
    if (context === playbackContext) callback.invokeMethodAsync("PlaybackStateChanged", playbackContext.state);
  };
  await context.audioWorklet.addModule(new URL("playback-worklet.js?v=20261104c", document.baseURI));
  player = new AudioWorkletNode(context, "ct-playback");
  player.port.onmessage = event => {
    if (context === playbackContext && typeof event.data.buffering === "boolean")
      callback.invokeMethodAsync("PlaybackBufferingChanged", event.data.buffering);
  };
  compressor = context.createDynamicsCompressor();
  compressor.threshold.value = -24;
  compressor.knee.value = 24;
  compressor.ratio.value = 2.5;
  compressor.attack.value = 0.01;
  compressor.release.value = 0.25;
  volumeNode = context.createGain();
  player.connect(compressor).connect(volumeNode).connect(context.destination);
  setPlaybackSettings(mode, volume);
  const connection = new WebSocket(url);
  socket = connection;
  connection.binaryType = "arraybuffer";
  let receivedAudio = false;
  connection.onmessage = event => {
    if (socket !== connection) return;
    const bytes = event.data;
    const header = new DataView(bytes);
    if (bytes.byteLength < 24 || header.getUint32(0, false) !== 0x43543031 ||
        header.getInt32(20, true) !== 24000) return;
    player?.port.postMessage({ pcm: bytes.slice(24) });
    if (!receivedAudio) {
      receivedAudio = true;
      callback.invokeMethodAsync("AudioPacketReceived");
    }
  };
  await new Promise((resolve, reject) => {
    connection.onopen = resolve;
    connection.onerror = () => reject(new Error("Could not connect to the live audio stream."));
  });
  connection.onclose = event => {
    if (socket !== connection || stopping) return;
    callback.invokeMethodAsync("PlaybackEnded", event.reason || `The audio connection ended (code ${event.code}).`);
    stopPlayback();
  };
  await playbackContext.resume();
  return playbackContext.state;
}

export function setPlaybackSettings(mode, volume) {
  player?.port.postMessage({ mode });
  if (volumeNode) volumeNode.gain.setTargetAtTime(
    Math.max(0, Math.min(2, Number(volume) / 100)), context.currentTime, 0.02);
}

export async function stopPlayback() {
  stopping = true;
  const closingSocket = socket;
  socket = null;
  if (closingSocket && closingSocket.readyState === WebSocket.OPEN) closingSocket.close();
  player?.port.postMessage({ reset: true });
  player?.disconnect();
  player = null;
  compressor?.disconnect();
  compressor = null;
  volumeNode?.disconnect();
  volumeNode = null;
  if (context) context.onstatechange = null;
  if (context && context.state !== "closed") await context.close();
  context = null;
}
