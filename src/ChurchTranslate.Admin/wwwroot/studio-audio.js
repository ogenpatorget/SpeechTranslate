let stream, context, source, processor, socket, stopping = false;
function warnBeforeLeaving(event) {
  event.preventDefault();
  event.returnValue = "";
}

export async function listInputs() {
  const permission = await navigator.mediaDevices.getUserMedia({ audio: true });
  permission.getTracks().forEach(track => track.stop());
  const devices = await navigator.mediaDevices.enumerateDevices();
  return devices.filter(x => x.kind === "audioinput").map(x => ({ id: x.deviceId, label: x.label || "Audio input" }));
}

export async function startCapture(url, deviceId, callback) {
  stopping = false;
  stream = await navigator.mediaDevices.getUserMedia({
    audio: { deviceId: deviceId ? { exact: deviceId } : undefined, channelCount: 1,
      echoCancellation: false, noiseSuppression: false, autoGainControl: false }
  });
  try {
    context = new AudioContext();
    await context.audioWorklet.addModule(new URL("capture-worklet.js", document.baseURI));
    source = context.createMediaStreamSource(stream);
    processor = new AudioWorkletNode(context, "ct-capture");
    const silence = context.createGain();
    silence.gain.value = 0;
    source.connect(processor).connect(silence).connect(context.destination);
    socket = new WebSocket(url);
    socket.binaryType = "arraybuffer";
    await new Promise((resolve, reject) => {
      socket.onopen = resolve;
      socket.onerror = () => reject(new Error("Could not connect the audio stream."));
    });
    processor.port.onmessage = event => {
      if (event.data.pcm && socket.readyState === WebSocket.OPEN && socket.bufferedAmount < 96000)
        socket.send(event.data.pcm);
      if (event.data.level !== undefined) callback.invokeMethodAsync("InputLevel", event.data.level);
    };
    socket.onclose = event => {
      if (!stopping) callback.invokeMethodAsync("AudioStopped", event.reason || "The audio connection ended.");
      stopCapture();
    };
    window.addEventListener("beforeunload", warnBeforeLeaving);
    await context.resume();
  } catch (error) {
    await stopCapture();
    throw error;
  }
}

export async function stopCapture() {
  stopping = true;
  window.removeEventListener("beforeunload", warnBeforeLeaving);
  if (socket && socket.readyState === WebSocket.OPEN) socket.close();
  socket = null;
  processor?.disconnect();
  source?.disconnect();
  stream?.getTracks().forEach(track => track.stop());
  if (context && context.state !== "closed") await context.close();
  processor = source = stream = context = null;
}
