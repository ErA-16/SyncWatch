// room.js — logic for room.html

const params = new URLSearchParams(window.location.search);
const roomCode = (params.get("code") || "").toUpperCase();

const creds = SyncWatchStorage.load(roomCode);
if (!roomCode || !creds) {
  window.location.href = `index.html`;
  throw new Error("No stored credentials for this room — redirecting.");
}

const video = document.getElementById("player");
const playPauseBtn = document.getElementById("play-pause-btn");
const skipBackBtn = document.getElementById("skip-back-btn");
const skipFwdBtn = document.getElementById("skip-fwd-btn");
const seekBar = document.getElementById("seek-bar");
const timeCurrent = document.getElementById("time-current");
const timeTotal = document.getElementById("time-total");
const episodeListEl = document.getElementById("episode-list");
const episodeCountEl = document.getElementById("episode-count");
const participantListEl = document.getElementById("participant-list");
const participantCountEl = document.getElementById("participant-count");
const toastEl = document.getElementById("toast");
const roomCodeDisplay = document.getElementById("room-code-display");

roomCodeDisplay.textContent = roomCode;

let roomData = null;      // last GetRoom() response
let currentMovieId = null;
let userIsScrubbing = false;
let playbackStateExists = false;   // true once a real PlaybackState has been created server-side (first Play)

let lastAppliedVersion = -1;
let lastCommandVersion = -1;
let serverStatus = null;
let pendingSeekPosition = null;
let targetPosition = null;
let targetReceivedAt = 0;
let smoothedRttMs = 120;
let autoplayBlocked = false;
let warnedAboutStatus = false;
let ownEchoExpected = false;
let ownEchoPosition = null;
let ownEchoTimer = null;

const SERVER_TICK_SECONDS = 5;
const HARD_SEEK_THRESHOLD_SECONDS = 8;
const SOFT_SYNC_DEADBAND_SECONDS = 0.4;
const PLAYBACK_RATE_MIN = 0.92;
const PLAYBACK_RATE_MAX = 1.08;
const PLAYBACK_RATE_GAIN = 0.25;
const OWN_ECHO_WINDOW_MS = 2500;
const RESUME_SEEK_TOLERANCE_SECONDS = 0.5;

const HAVE_METADATA = 1;
const HAVE_FUTURE_DATA = 3;

function normalizeStatus(value) {
  if (value === "Playing" || value === "Paused") return value;
  if (value === 0 || value === "0") return "Playing";
  if (value === 1 || value === "1") return "Paused";
  return null;
}

// --- Toast ---

function showToast(message) {
  toastEl.textContent = message;
  toastEl.classList.add("show");
  setTimeout(() => toastEl.classList.remove("show"), 2200);
}

// --- Invite copy ---

document.getElementById("copy-invite-btn").addEventListener("click", async () => {
  try {
    await navigator.clipboard.writeText(roomCode);
    showToast("Room code copied");
  } catch {
    showToast(`Room code: ${roomCode}`);
  }
});

// --- Time formatting ---

function formatTime(totalSeconds) {
  const s = Math.max(0, Math.floor(totalSeconds || 0));
  const m = Math.floor(s / 60);
  const sec = s % 60;
  return `${m}:${sec.toString().padStart(2, "0")}`;
}

function isUsablePosition(value) {
  return typeof value === "number" && Number.isFinite(value) && value >= 0;
}

function safePosition(value, fallback = 0) {
  return isUsablePosition(value) ? value : fallback;
}

function clampPosition(value) {
  if (!isUsablePosition(value)) return 0;
  if (isUsablePosition(video.duration)) return Math.min(value, video.duration);
  return value;
}

function hasMetadata() {
  return video.readyState >= HAVE_METADATA;
}

function hasEnoughData() {
  return video.readyState >= HAVE_FUTURE_DATA;
}

async function invokeTimed(method, position, ...args) {
  const startedAt = performance.now();
  expectOwnEcho(position);
  try {
    return await connection.invoke(method, ...args);
  } finally {
    const rtt = performance.now() - startedAt;
    smoothedRttMs = smoothedRttMs * 0.7 + rtt * 0.3;
  }
}

// Every command we send is broadcast straight back to us by the Hub. That echo
// carries the position we ourselves reported, so re-seeking to it would undo the
// RTT's worth of playback that already happened locally — which is the half-RTT
// rewind and re-buffer on every resume. Remember the position we sent so the echo
// can be recognised and the local clock trusted instead.
function expectOwnEcho(position) {
  ownEchoExpected = true;
  ownEchoPosition = safePosition(position);
  clearTimeout(ownEchoTimer);
  ownEchoTimer = setTimeout(clearOwnEcho, OWN_ECHO_WINDOW_MS);
}

function clearOwnEcho() {
  ownEchoExpected = false;
  ownEchoPosition = null;
  clearTimeout(ownEchoTimer);
  ownEchoTimer = null;
}

// Only our own command comes back carrying the exact position we reported, so a
// mismatch means the other participant is driving and their position must win.
function consumeOwnEcho(isFreshCommand, position) {
  if (!isFreshCommand || !ownEchoExpected) return false;

  const wasOurs = Math.abs(safePosition(position) - ownEchoPosition) <= RESUME_SEEK_TOLERANCE_SECONDS;
  clearOwnEcho();
  return wasOurs;
}

// --- Load room state (episodes, participants) ---
const ROOM_POLL_MS = 4000;
let roomPollTimer = null;
let lastRoomSignature = null;
let roomRefreshInFlight = false;

function roomSignature(data) {
  const people = data.participants
    .map(p => `${p.displayName}|${p.isHost}|${p.status}`)
    .sort()
    .join(",");
  const movies = data.movies
    .map(m => `${m.id}|${m.episodeNumber}|${m.title}|${m.duration}`)
    .sort()
    .join(",");
  return `${people}#${movies}`;
}

async function refreshRoom() {
  if (roomRefreshInFlight) return false;
  roomRefreshInFlight = true;
  try {
    const fresh = await SyncWatchAPI.getRoom(roomCode);
    const signature = roomSignature(fresh);
    if (signature === lastRoomSignature) return false;

    lastRoomSignature = signature;
    roomData = fresh;
    renderEpisodes();
    renderParticipants();
    return true;
  } catch (err) {
    console.warn("Room refresh failed:", err);
    return false;
  } finally {
    roomRefreshInFlight = false;
  }
}

function startRoomPolling() {
  if (roomPollTimer) return;
  roomPollTimer = setInterval(() => {
    if (document.hidden) return;
    refreshRoom();
  }, ROOM_POLL_MS);
}

document.addEventListener("visibilitychange", () => {
  if (!document.hidden && roomPollTimer) refreshRoom();
});

async function loadRoom() {
  roomData = await SyncWatchAPI.getRoom(roomCode);
  lastRoomSignature = roomSignature(roomData);
  renderEpisodes();
  renderParticipants();

  if (creds.isHost) {
    document.getElementById("close-room-btn").style.display = "inline-block";
  }
}

function renderEpisodes() {
  const movies = [...roomData.movies].sort((a, b) => a.episodeNumber - b.episodeNumber);
  episodeCountEl.textContent = movies.length;

  const showEpisodeNumbers = movies.length > 1; // a single uploaded file isn't really "episode 1" of anything

  episodeListEl.innerHTML = "";

  if (movies.length === 0) {
    episodeListEl.innerHTML = "<p>Nothing uploaded yet.</p>";
    return;
  }

  movies.forEach(m => {
    const row = document.createElement("div");
    row.className = "episode-row" + (m.id === currentMovieId ? " active" : "");
    row.dataset.movieId = m.id;

    const left = document.createElement("span");
    if (showEpisodeNumbers) {
      const epNum = document.createElement("span");
      epNum.className = "ep-num";
      epNum.textContent = `${m.episodeNumber} `;
      left.appendChild(epNum);
    }
    left.appendChild(document.createTextNode(m.title)); // title is a user-controlled filename — textContent, never innerHTML

    const timeEl = document.createElement("span");
    timeEl.className = "time";
    timeEl.textContent = m.duration > 0 ? formatTime(m.duration) : "--:--";

    row.append(left, timeEl);
    row.addEventListener("click", () => selectEpisode(m.id));
    episodeListEl.appendChild(row);
  });
}

function renderParticipants() {
  participantCountEl.textContent = roomData.participants.length;
  participantListEl.innerHTML = "";

  roomData.participants.forEach(p => {
    const row = document.createElement("div");
    row.className = "participant-row";

    const dot = document.createElement("span");
    dot.className = "dot" + (p.status === "Online" ? " online" : "");

    const nameEl = document.createElement("span");
    nameEl.textContent = p.displayName || "Guest"; // user-controlled — textContent, not innerHTML

    row.append(dot, nameEl);

    if (p.isHost) {
      const hostTag = document.createElement("span");
      hostTag.className = "host-tag";
      hostTag.textContent = "Host";
      row.appendChild(hostTag);
    }

    participantListEl.appendChild(row);
  });
}

// --- SignalR connection ---

const connection = new signalR.HubConnectionBuilder()
  .withUrl("/hubs/playback")
  .withAutomaticReconnect()
  .build();

connection.on("PlaybackUpdated", (state) => {
  applyServerState(state);
});

connection.on("ChatMessageReceived", (msg) => {
  appendChatMessage(msg.displayName, msg.message);
});

connection.on("ParticipantsUpdated", () => {
  refreshRoom();
});

connection.onreconnecting(() => {
  showToast("Reconnecting…");
});

connection.onreconnected(async () => {
  await connection.invoke("Reconnect", creds.roomId, creds.token);
  await SyncWatchAPI.reconnectRoom(creds.token);
  await refreshRoom();
  showToast("Reconnected");
});

async function startConnection() {
  try {
    await connection.start();
    await connection.invoke("Join", creds.roomId, creds.token);
  } catch (err) {
    showToast("Could not connect — retrying…");
    setTimeout(startConnection, 3000);
  }
}

window.addEventListener("pagehide", (event) => {
  if (event.persisted) return;
  SyncWatchAPI.leaveRoom(creds.token);
});

window.addEventListener("pageshow", (event) => {
  if (!event.persisted) return;
  SyncWatchAPI.reconnectRoom(creds.token).then(refreshRoom);
});

// --- Applying server state to the local video element ---

function applyServerState(state) {
  const version = Number.isFinite(state.version) ? state.version : 0;

  if (version < lastAppliedVersion) return;
  lastAppliedVersion = version;

  playbackStateExists = true;

  const isFreshCommand = version > lastCommandVersion;
  if (isFreshCommand) lastCommandVersion = version;

  const isOwnEcho = consumeOwnEcho(isFreshCommand, state.position);

  const serverMovieId = state.currentMovieId;
  const incomingStatus = normalizeStatus(state.status);
  serverStatus = incomingStatus;

  // Defensive clamp: a PlaybackState left "Playing" with nobody watching keeps
  // accumulating position forever (Sync has no way to know the video actually
  // ended). Without this, a stale state can broadcast a position past the
  // real video's length, snapping playback to the very end.
  const rawPosition = safePosition(state.position);

  if (serverMovieId && serverMovieId !== currentMovieId) {
    currentMovieId = serverMovieId;
    targetPosition = null;
    pendingSeekPosition = null;
    resetPlaybackRate();
    video.src = SyncWatchAPI.streamUrl(serverMovieId);
    renderEpisodes();
  }

  if (isFreshCommand) {
    if (isOwnEcho && serverStatus === "Playing" && hasMetadata()) {
      setTarget(clampPosition(safePosition(video.currentTime)));
    } else {
      const compensated = serverStatus === "Playing"
          ? rawPosition + smoothedRttMs / 2000
          : rawPosition;
      setTarget(clampPosition(compensated));
      applyPosition(targetPosition);
    }
  } else if (serverStatus === "Playing") {
    setTarget(clampPosition(rawPosition - smoothedRttMs / 2000));
  }

  if (serverStatus === null) {
    if (!warnedAboutStatus) {
      warnedAboutStatus = true;
      console.warn("Unrecognised playback status from server:", state.status);
      showToast("Unexpected playback status from server.");
    }
    return;
  }

  if (serverStatus === "Paused") {
    if (!video.paused) video.pause();
    resetPlaybackRate();
  } else {
    ensurePlaying();
  }

  playPauseBtn.innerHTML = serverStatus === "Playing" ? "&#10074;&#10074;" : "&#9654;";
}

function applyPosition(position) {
  if (!hasMetadata()) {
    pendingSeekPosition = position;
    return;
  }

  pendingSeekPosition = null;

  if (Math.abs(video.currentTime - position) > RESUME_SEEK_TOLERANCE_SECONDS) {
    video.currentTime = position;
  }
  resetPlaybackRate();
}

function resetPlaybackRate() {
  if (video.playbackRate !== 1) video.playbackRate = 1;
}

function setTarget(position) {
  targetPosition = position;
  targetReceivedAt = performance.now();
}

function projectedTarget() {
  if (targetPosition === null) return null;
  const elapsed = (performance.now() - targetReceivedAt) / 1000;
  return clampPosition(targetPosition + elapsed);
}

function softSyncStep() {
  const target = projectedTarget();
  if (target === null) return;
  if (userIsScrubbing || video.paused || video.seeking) return;
  if (!hasEnoughData()) { resetPlaybackRate(); return; }

  const drift = video.currentTime - target;

  if (Math.abs(drift) >= HARD_SEEK_THRESHOLD_SECONDS) {
    video.currentTime = target;
    resetPlaybackRate();
    return;
  }

  if (Math.abs(drift) <= SOFT_SYNC_DEADBAND_SECONDS) {
    resetPlaybackRate();
    return;
  }

  const rate = 1 - drift * PLAYBACK_RATE_GAIN;
  video.playbackRate = Math.min(PLAYBACK_RATE_MAX, Math.max(PLAYBACK_RATE_MIN, rate));
}

async function ensurePlaying() {
  if (!video.paused || !currentMovieId) return;
  if (!hasMetadata()) return;
  if (autoplayBlocked) return;

  try {
    await video.play();
    autoplayBlocked = false;
  } catch (err) {
    const name = err && err.name;
    if (name === "NotAllowedError") {
      autoplayBlocked = true;
      showToast("Browser blocked autoplay — press play.");
    } else if (name === "AbortError") {
    } else {
      showToast("Could not start playback.");
    }
  }
}

// --- Local controls, each sends a command to the Hub ---

async function sendPlay() {
  await invokeTimed("Play", video.currentTime, creds.roomId, {
    movieId: currentMovieId,
    position: safePosition(video.currentTime)
  });
}

async function sendPause() {
  const position = safePosition(video.currentTime, isUsablePosition(video.duration) ? video.duration : 0);
  await invokeTimed("Pause", position, creds.roomId, {
    movieId: currentMovieId,
    position
  });
}

async function sendSeek(position) {
  await invokeTimed("Seek", position, creds.roomId, {
    movieId: currentMovieId,
    position: safePosition(position)
  });
}

// Called when a user clicks an episode row. Two different cases:
// - No PlaybackState exists yet (fresh room, nobody's pressed Play) — just
//   select it locally so Play has a movieId to send. Nothing hits the server;
//   SwitchEpisode would fail since there's nothing to switch FROM yet.
// - A PlaybackState already exists — this really is a mid-watch switch, so it
//   goes through the server (and syncs the other participant immediately).
async function selectEpisode(movieId) {
  if (!playbackStateExists) {
    currentMovieId = movieId;
    targetPosition = null;
    pendingSeekPosition = 0;
    resetPlaybackRate();
    video.src = SyncWatchAPI.streamUrl(movieId);
    renderEpisodes();
    return;
  }

  try {
    await invokeTimed("SwitchEpisode", 0, creds.roomId, { movieId, position: 0 });
  } catch (err) {
    showToast(err.message || "Could not switch episode.");
  }
}

playPauseBtn.addEventListener("click", async () => {
  if (!currentMovieId) {
    showToast("Pick something to watch first.");
    return;
  }
  try {
    if (video.paused) {
      if (hasMetadata()) {
        await video.play();
        autoplayBlocked = false;
      } else {
        pendingSeekPosition = safePosition(video.currentTime);
      }
      resetPlaybackRate();
      await sendPlay();
    } else {
      video.pause();
      resetPlaybackRate();
      await sendPause();
    }
  } catch (err) {
    if (err && err.name === "NotAllowedError") {
      showToast("Browser blocked playback — tap play again.");
    } else {
      showToast(err.message || "Playback command failed.");
    }
  }
});

skipBackBtn.addEventListener("click", async () => {
  if (!currentMovieId) return;
  await sendSeek(Math.max(0, safePosition(video.currentTime) - 10));
});

skipFwdBtn.addEventListener("click", async () => {
  if (!currentMovieId) return;
  await sendSeek(safePosition(video.currentTime) + 10);
});

seekBar.addEventListener("mousedown", () => { userIsScrubbing = true; showControls(); });
seekBar.addEventListener("touchstart", () => { userIsScrubbing = true; showControls(); });
video.addEventListener("mousedown", showControls);
video.addEventListener("touchstart", showControls);
video.addEventListener("play", showControls);
video.addEventListener("pause", showControls);

seekBar.addEventListener("input", () => {
  userIsScrubbing = true;
  if (isUsablePosition(video.duration)) {
    timeCurrent.textContent = formatTime((seekBar.value / 100) * video.duration);
  }
});

seekBar.addEventListener("change", async () => {
  userIsScrubbing = false;
  if (!currentMovieId) return;
  if (!isUsablePosition(video.duration)) return;
  await sendSeek((seekBar.value / 100) * video.duration);
});

video.addEventListener("loadedmetadata", () => {
  if (pendingSeekPosition !== null) {
    const wanted = pendingSeekPosition;
    pendingSeekPosition = null;
    video.currentTime = clampPosition(wanted);
  }
  timeTotal.textContent = formatTime(video.duration);
  if (serverStatus === "Playing") ensurePlaying();
  showControls();
});

video.addEventListener("canplay", () => {
  if (serverStatus === "Playing") ensurePlaying();
});

video.addEventListener("waiting", resetPlaybackRate);
video.addEventListener("stalled", resetPlaybackRate);
video.addEventListener("seeking", resetPlaybackRate);

video.addEventListener("error", () => {
  const code = video.error ? video.error.code : 0;
  showToast(code === 4 ? "This video format isn't supported by your browser." : "Video failed to load.");
});

// --- Auto-hide playback controls ---

let controlTimeout = null;
let isLandscape = false;

function checkOrientation() {
  isLandscape = window.matchMedia("(orientation: landscape)").matches;
}

checkOrientation();
window.addEventListener("orientationchange", checkOrientation);

video.addEventListener("mousemove", showControls);
controlsEl.addEventListener("mousemove", showControls);

function hideControls() {
  if (!isLandscape) {
    controlTimeout = null;
    return;
  }
  const controls = document.querySelector(".controls");
  if (controls) controls.style.display = "none";
}

function showControls() {
  const controls = document.querySelector(".controls");
  if (controls) controls.style.display = "";
  clearTimeout(controlTimeout);
  controlTimeout = setTimeout(hideControls, 3000);
}

video.addEventListener("timeupdate", () => {
  softSyncStep();

  if (userIsScrubbing) return;
  timeCurrent.textContent = formatTime(video.currentTime);
  timeTotal.textContent = formatTime(video.duration);
  if (isUsablePosition(video.duration)) {
    seekBar.value = (video.currentTime / video.duration) * 100;
  }
});

// --- Chat ---

const chatBox = document.getElementById("chat-box");
const chatInput = document.getElementById("chat-input");
const chatSendBtn = document.getElementById("chat-send-btn");

function appendChatMessage(displayName, message) {
  // built with textContent, not innerHTML — both displayName and message are
  // user-typed and broadcast to the other participant, so neither should be
  // interpreted as HTML (that's how a <script> in a chat message would run)
  const row = document.createElement("div");
  row.className = "chat-msg";

  const nameEl = document.createElement("b");
  nameEl.textContent = displayName;

  const textEl = document.createElement("span");
  textEl.className = "text";
  textEl.textContent = message;

  row.append(nameEl, textEl);
  chatBox.appendChild(row);
  chatBox.scrollTop = chatBox.scrollHeight;
}

async function sendChatMessage() {
  const text = chatInput.value.trim();
  if (!text) return;
  chatInput.value = "";
  try {
    await connection.invoke("SendMessage", creds.roomId, creds.displayName, text);
  } catch (err) {
    showToast("Message failed to send.");
  }
}

chatSendBtn.addEventListener("click", sendChatMessage);
chatInput.addEventListener("keydown", (e) => {
  if (e.key === "Enter") sendChatMessage();
});

// --- Close room (host only) ---

document.getElementById("close-room-btn").addEventListener("click", async () => {
  const confirmed = confirm("Close this room? This deletes it and its uploaded movies for both of you — this can't be undone.");
  if (!confirmed) return;

  try {
    await SyncWatchAPI.closeRoom(creds.roomId, creds.token);
    localStorage.removeItem(`syncwatch:${roomCode}`);
    window.location.href = "index.html";
  } catch (err) {
    showToast(err.message || "Could not close the room.");
  }
});

// --- Fullscreen ---

const playerContainer = document.getElementById("player-container");
const fullscreenBtn = document.getElementById("fullscreen-btn");

function isTouchDevice() {
  return window.matchMedia("(pointer: coarse)").matches;
}

function inFullscreen() {
  return document.fullscreenElement === playerContainer
    || document.webkitFullscreenElement === playerContainer
    || video.webkitDisplayingFullscreen;
}

async function lockLandscape() {
  const orientation = screen.orientation;
  if (!orientation || typeof orientation.lock !== "function") return false;
  try {
    await orientation.lock("landscape");
    return true;
  } catch {
    return false;
  }
}

function unlockOrientation() {
  const orientation = screen.orientation;
  if (orientation && typeof orientation.unlock === "function") orientation.unlock();
}

async function syncFullscreenState() {
  const active = inFullscreen();
  fullscreenBtn.innerHTML = active ? "&#10005;" : "&#9974;";
  fullscreenBtn.title = active ? "Exit fullscreen" : "Fullscreen";
  fullscreenBtn.setAttribute("aria-label", fullscreenBtn.title);
  if (!active) {
    unlockOrientation();
    return;
  }
  const locked = await lockLandscape();
  if (!locked && isTouchDevice()) showToast("Rotate your device for the best view.");
}

fullscreenBtn.addEventListener("click", async () => {
  try {
    if (inFullscreen()) {
      if (video.webkitDisplayingFullscreen && video.webkitExitFullscreen) {
        video.webkitExitFullscreen();
        return;
      }
      const exit = document.exitFullscreen || document.webkitExitFullscreen;
      if (exit) await exit.call(document);
      return;
    }
    const request = playerContainer.requestFullscreen || playerContainer.webkitRequestFullscreen;
    if (request) {
      await request.call(playerContainer, { navigationUI: "hide" });
      if (!inFullscreen()) showToast("Could not enter fullscreen.");
      return;
    }
    if (video.webkitEnterFullscreen) {
      video.webkitEnterFullscreen();
      return;
    }
    showToast("Fullscreen is not supported on this browser.");
  } catch {
    showToast("Could not enter fullscreen.");
  }
});

document.addEventListener("fullscreenchange", syncFullscreenState);
document.addEventListener("webkitfullscreenchange", syncFullscreenState);
video.addEventListener("webkitbeginfullscreen", syncFullscreenState);
video.addEventListener("webkitendfullscreen", syncFullscreenState);

syncFullscreenState();

video.addEventListener("ended", async () => {
  if (!currentMovieId) return;
  resetPlaybackRate();
  try {
    await invokeTimed("Advance", 0, creds.roomId, {
      movieId: currentMovieId,
      position: 0
    });
  } catch (err) {
    console.warn("Advance request failed:", err);
  }
});

// --- Boot ---

(async function init() {
  try {
    await loadRoom();
    await startConnection();
    startRoomPolling();
  } catch (err) {
    console.error("Room init failed:", err);
    showToast(err.message || "Could not load this room.");
  }
})();

