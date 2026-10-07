// room.js — logic for room.html

// --- Session bootstrap ------------------------------------------------------

const params = new URLSearchParams(window.location.search);
const roomCode = (params.get("code") || "").trim().toUpperCase();
const creds = roomCode ? SyncWatchStorage.load(roomCode) : null;

if (!roomCode || !creds) {
  window.location.replace("index.html");
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
const copyInviteBtn = document.getElementById("copy-invite-btn");
const closeRoomBtn = document.getElementById("close-room-btn");
const chatBox = document.getElementById("chat-box");
const chatInput = document.getElementById("chat-input");
const chatSendBtn = document.getElementById("chat-send-btn");
const playerContainer = document.getElementById("player-container");
const controls = document.querySelector(".controls");
const fullscreenBtn = document.getElementById("fullscreen-btn");

roomCodeDisplay.textContent = roomCode;

// --- Sync tuning constants --------------------------------------------------

const HAVE_METADATA = 1;
const HAVE_FUTURE_DATA = 3;
const ROOM_POLL_MS = 4000;
const SERVER_TICK_SECONDS = 5;
const HARD_SEEK_THRESHOLD_SECONDS = 8;
const SOFT_SYNC_DEADBAND_SECONDS = 0.4;
const PLAYBACK_RATE_MIN = 0.92;
const PLAYBACK_RATE_MAX = 1.08;
const PLAYBACK_RATE_GAIN = 0.25;
const OWN_ECHO_WINDOW_MS = 2500;
const RESUME_SEEK_TOLERANCE_SECONDS = 0.5;
// How long the fullscreen control bar stays up after the last pointer activity.
const CONTROLS_IDLE_MS = 3000;
// How often to re-check while a hide is blocked but the idle window has
// already elapsed. Only reached mid-scrub, so a coarse interval is fine.
const CONTROLS_RECHECK_MS = 400;

// --- Mutable sync state -----------------------------------------------------

let roomData = null;
let currentMovieId = null;
let userIsScrubbing = false;
let playbackStateExists = false;
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
let ownEchoSentAt = 0;
let ownEchoTimer = null;
let lastRoomSignature = null;
let roomRefreshInFlight = false;
let roomPollTimer = null;
let toastTimer = null;
let controlTimeout = null;
let controlsVisible = true;
let lastActivityAt = Date.now();
let isFullscreen = false;
let leavingIntentionally = false;
let scrubTarget = null;
const seenChatMessageIds = new Set();

// --- Toast ------------------------------------------------------------------

function showToast(message) {
  toastEl.textContent = message;
  toastEl.classList.add("show");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => toastEl.classList.remove("show"), 2200);
}

// --- Position helpers -------------------------------------------------------

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

function normalizeStatus(value) {
  if (value === "Playing" || value === "Paused") return value;
  if (value === 0 || value === "0") return "Playing";
  if (value === 1 || value === "1") return "Paused";
  return null;
}

// --- SignalR connection -----------------------------------------------------

const connection = new signalR.HubConnectionBuilder()
  .withUrl("/hubs/playback")
  .withAutomaticReconnect()
  .build();

function isConnected() {
  return connection.state === signalR.HubConnectionState.Connected;
}

// Every command we send is broadcast straight back to us by the Hub. That echo
// carries the position we ourselves reported, so re-seeking to it would undo the
// RTT's worth of playback that already happened locally — which is the half-RTT
// rewind and re-buffer on every resume. Remember the position we sent so the echo
// can be recognised and the local clock trusted instead.
function expectOwnEcho(position) {
  ownEchoExpected = true;
  ownEchoPosition = safePosition(position);
  ownEchoSentAt = performance.now();
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
// Returns the position we asked for, projected forward over the time the command
// spent in flight, or null when this broadcast is somebody else's.
function consumeOwnEcho(isFreshCommand, position) {
  if (!isFreshCommand || !ownEchoExpected) return null;

  const wasOurs = Math.abs(safePosition(position) - ownEchoPosition) <= RESUME_SEEK_TOLERANCE_SECONDS;
  const projected = ownEchoPosition + (performance.now() - ownEchoSentAt) / 1000;
  clearOwnEcho();
  return wasOurs ? projected : null;
}

// The Hub methods take (roomId, request) — nothing else. The local `position`
// argument here is only for own-echo tracking and must never be sent over the wire.
async function invokeTimed(method, position, ...args) {
  if (!isConnected()) {
    showToast("Still reconnecting — try again in a moment.");
    throw new Error("Not connected to the room.");
  }

  expectOwnEcho(position);
  const startedAt = performance.now();
  try {
    return await connection.invoke(method, ...args);
  } finally {
    const rtt = performance.now() - startedAt;
    smoothedRttMs = smoothedRttMs * 0.7 + rtt * 0.3;
  }
}

// --- Room state (collection + participants) ----------------------------------

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

function renderEpisodes() {
  if (!roomData) return;

  const movies = [...roomData.movies].sort((a, b) => a.episodeNumber - b.episodeNumber);
  episodeCountEl.textContent = movies.length;

  // A single uploaded file isn't really "episode 1" of anything.
  const showEpisodeNumbers = movies.length > 1;

  episodeListEl.innerHTML = "";

  if (movies.length === 0) {
    const empty = document.createElement("p");
    empty.textContent = "Nothing uploaded yet.";
    episodeListEl.appendChild(empty);
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
    // title is a user-controlled filename — textContent, never innerHTML
    left.appendChild(document.createTextNode(m.title));

    const timeEl = document.createElement("span");
    timeEl.className = "time";
    timeEl.textContent = m.duration > 0 ? formatTime(m.duration) : "--:--";

    row.append(left, timeEl);
    row.addEventListener("click", () => selectEpisode(m.id));
    episodeListEl.appendChild(row);
  });
}

function renderParticipants() {
  if (!roomData) return;

  participantCountEl.textContent = roomData.participants.length;
  participantListEl.innerHTML = "";

  roomData.participants.forEach(p => {
    const row = document.createElement("div");
    row.className = "participant-row";

    const dot = document.createElement("span");
    dot.className = "dot" + (p.status === "Online" ? " online" : "");

    // user-controlled — textContent, not innerHTML
    const nameEl = document.createElement("span");
    nameEl.textContent = p.displayName || "Guest";

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

  closeRoomBtn.style.display = creds.isHost ? "inline-block" : "none";
}

// --- Hub wiring -------------------------------------------------------------

connection.on("PlaybackUpdated", state => {
  applyServerState(state);
});

// Replayed by the Hub on join and reconnect, so a refresh no longer empties the
// panel. It replaces whatever is on screen because it is the full history.
connection.on("ChatHistory", messages => {
  chatBox.innerHTML = "";
  seenChatMessageIds.clear();
  (messages || []).forEach(renderChatMessage);
});

connection.on("ChatMessageReceived", msg => {
  renderChatMessage(msg);
  announceChatMessage(msg);
});

// Sent with a payload when someone drops, so the nudge can name them instead of
// the client having to diff the roster itself.
connection.on("ParticipantsUpdated", change => {
  announcePresenceChange(change);
  refreshRoom();
});

connection.onreconnecting(() => {
  showToast("Reconnecting…");
});

connection.onreconnected(async () => {
  try {
    await connection.invoke("Reconnect", creds.roomId, creds.token);
    await SyncWatchAPI.reconnectRoom(creds.token);
    await refreshRoom();
    showToast("Reconnected");
  } catch (err) {
    console.warn("Rejoin failed:", err);
    showToast("Reconnected, but could not rejoin the room.");
  }
});

connection.onclose(() => {
  showToast("Disconnected — reload to rejoin.");
});

async function startConnection() {
  try {
    if (connection.state === signalR.HubConnectionState.Disconnected) {
      await connection.start();
    }
    await connection.invoke("Join", creds.roomId, creds.token);
  } catch (err) {
    console.warn("Connection attempt failed:", err);
    showToast("Could not connect — retrying…");
    setTimeout(startConnection, 3000);
  }
}

window.addEventListener("pagehide", event => {
  if (event.persisted) return;
  if (leavingIntentionally) return;
  SyncWatchAPI.leaveRoom(creds.token);
});

window.addEventListener("pageshow", event => {
  if (!event.persisted) return;
  SyncWatchAPI.reconnectRoom(creds.token).then(refreshRoom);
});

// --- Applying server state to the local video element -----------------------

function applyServerState(state) {
  const version = Number.isFinite(state.version) ? state.version : 0;

  if (version < lastAppliedVersion) return;
  lastAppliedVersion = version;

  playbackStateExists = true;

  const isFreshCommand = version > lastCommandVersion;
  if (isFreshCommand) lastCommandVersion = version;

  const echoedPosition = consumeOwnEcho(isFreshCommand, state.position);
  const isOwnEcho = echoedPosition !== null;

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
      const desired = clampPosition(echoedPosition);
      setTarget(desired);
      // Play already moved the local player, but a seek or a skip never touched
      // it — so trust the local clock only while it is already sitting where we
      // asked it to be. Without this a skip during playback is swallowed here and
      // only lands on the next 5-second broadcast, by which time the second tap
      // has already sent it to +20.
      if (Math.abs(video.currentTime - desired) > RESUME_SEEK_TOLERANCE_SECONDS) {
        applyPosition(desired);
      }
    } else {
      // The server's position was measured half an RTT ago; add it back so
      // playback lands where the host actually is, not where they were.
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

// Nudge playbackRate instead of seeking whenever we're only slightly off, so
// ordinary drift never costs a re-buffer.
function softSyncStep() {
  const target = projectedTarget();
  if (target === null) return;
  if (userIsScrubbing || video.paused || video.seeking) return;
  if (!hasEnoughData()) {
    resetPlaybackRate();
    return;
  }

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
      // play() was interrupted by a pause or a new load — not an error worth surfacing
    } else {
      showToast("Could not start playback.");
    }
  }
}

// --- Local controls, each sends a command to the Hub ------------------------

function playbackCommand(movieId, position) {
  return { movieId, position: safePosition(position) };
}

// video.currentTime stays 0 until metadata lands, so a command sent while the
// element is still loading would report position 0 and restart the room from the
// beginning. Fall back to the last position the server gave us instead.
function localPosition(fallback = 0) {
  if (hasMetadata()) return safePosition(video.currentTime);
  const target = projectedTarget();
  return target !== null ? target : safePosition(video.currentTime, fallback);
}

async function sendPlay() {
  const position = localPosition();
  await invokeTimed("Play", position, creds.roomId, playbackCommand(currentMovieId, position));
}

async function sendPause() {
  const position = localPosition(isUsablePosition(video.duration) ? video.duration : 0);
  await invokeTimed("Pause", position, creds.roomId, playbackCommand(currentMovieId, position));
}

async function sendSeek(position) {
  const target = clampPosition(position);
  await invokeTimed("Seek", target, creds.roomId, playbackCommand(currentMovieId, target));
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
  try {
    await sendSeek(localPosition() - 10);
  } catch (err) {
    showToast(err.message || "Could not skip back.");
  }
});

skipFwdBtn.addEventListener("click", async () => {
  if (!currentMovieId) return;
  try {
    await sendSeek(localPosition() + 10);
  } catch (err) {
    showToast(err.message || "Could not skip forward.");
  }
});

// The bar is a 0-100 range, so its value only means anything against a known
// duration — and while dragging, only the value captured at that moment is the
// one the user aimed at.
function barValueToPosition() {
  if (!isUsablePosition(video.duration)) return null;
  return clampPosition((Number(seekBar.value) / 100) * video.duration);
}

seekBar.addEventListener("pointerdown", () => {
  userIsScrubbing = true;
  scrubTarget = barValueToPosition();
  showControls();
});

seekBar.addEventListener("input", () => {
  userIsScrubbing = true;
  scrubTarget = barValueToPosition();
  if (scrubTarget !== null) timeCurrent.textContent = formatTime(scrubTarget);
});

// Commits the position captured during the drag, never seekBar.value read now.
// The pointerup listener on window below ends the scrub, and a timeupdate landing
// in that gap rewrites the bar from video.currentTime — so reading the bar at
// commit time sends playback back to where it was instead of where the user
// dragged. Idempotent, because both change and that timeout can arrive.
function commitScrub(fromChangeEvent) {
  // change also fires for keyboard nudges on the bar, where no pointer ever
  // touched it and there is nothing captured to fall back on.
  if (!userIsScrubbing && !fromChangeEvent) return;

  userIsScrubbing = false;
  const target = scrubTarget !== null ? scrubTarget : barValueToPosition();
  scrubTarget = null;
  showControls(); // re-arm the idle countdown after a scrub

  if (target === null || !currentMovieId) return;
  if (Math.abs(video.currentTime - target) <= RESUME_SEEK_TOLERANCE_SECONDS) return;

  sendSeek(target).catch(err => showToast(err.message || "Could not seek."));
}

seekBar.addEventListener("change", () => commitScrub(true));

// A drag can end anywhere — pointerup on the window, not the bar. Without this
// userIsScrubbing stays true forever and soft sync silently stops correcting
// drift. Deferred by a tick so the range's own change event gets to commit first.
window.addEventListener("pointerup", () => {
  if (userIsScrubbing) setTimeout(() => commitScrub(false), 0);
});

// A cancelled drag never commits — the finger was lifted by the system, not the
// user, so playback stays where it was.
window.addEventListener("pointercancel", () => {
  userIsScrubbing = false;
  scrubTarget = null;
});

video.addEventListener("click", showControls);

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

// --- Auto-hide playback controls -------------------------------------------

function inFullscreen() {
  return document.fullscreenElement === playerContainer
    || document.webkitFullscreenElement === playerContainer
    || video.webkitDisplayingFullscreen;
}

// Any interaction anywhere on the player restamps the idle window. Bound to
// the container in capture phase so it also catches events on the controls and
// the video inside it.
function markActivity() {
  lastActivityAt = Date.now();
  // Calls setControlsVisible rather than showControls — showControls restamps
  // lastActivityAt and would recurse back into here.
  if (!controlsVisible) setControlsVisible(true);
  else if (inFullscreen() && !video.paused) scheduleHide();
}

// The bar is hidden by a CSS class, not inline styles, so the transition and
// pointer-events handling live in one place (styles.css) and can't drift apart.
function setControlsVisible(visible) {
  controls.classList.toggle("controls--hidden", !visible);
  controlsVisible = visible;

  clearTimeout(controlTimeout);
  controlTimeout = null;

  // Only ever auto-hide while actually watching in fullscreen. Outside
  // fullscreen the bar sits under the video and is just page furniture.
  if (visible && inFullscreen() && !video.paused) {
    scheduleHide();
  }
}

function showControls() {
  markActivity();
  setControlsVisible(true);
}

function hideControls() {
  // Never hide out from under someone mid-interaction. This is timestamp-based
  // rather than a `:hover` test on purpose: :hover latches on touch devices
  // after a tap and never clears, and it also blocks the hide while the desktop
  // pointer merely rests near the bottom edge — either way the bar never goes
  // away, because a vetoed hide schedules no retry.
  if (userIsScrubbing || Date.now() - lastActivityAt < CONTROLS_IDLE_MS) {
    scheduleHide();
    return;
  }
  setControlsVisible(false);
}

function scheduleHide() {
  clearTimeout(controlTimeout);

  const remaining = CONTROLS_IDLE_MS - (Date.now() - lastActivityAt);

  // When already past the idle window but still vetoed (mid-scrub, say) the
  // remaining time is <= 0. Scheduling 0ms there would re-fire on the next tick
  // and spin the timer queue forever, so poll on a floor interval instead.
  controlTimeout = setTimeout(
    hideControls,
    remaining > 0 ? remaining : CONTROLS_RECHECK_MS
  );
}

// Pointer activity over the player — mouse, pen or touch, one code path — keeps
// the bar awake and refreshes the countdown even while it's already visible.
playerContainer.addEventListener("pointermove", markActivity, { passive: true });
playerContainer.addEventListener("pointerdown", markActivity, { passive: true });
playerContainer.addEventListener("touchstart", markActivity, { passive: true });

// Keep it visible while paused — nothing to distract from, and hiding the
// controls on a paused video just looks broken.
video.addEventListener("play", showControls);
video.addEventListener("pause", showControls);

video.addEventListener("timeupdate", () => {
  softSyncStep();

  if (userIsScrubbing) return;

  timeCurrent.textContent = formatTime(video.currentTime);
  timeTotal.textContent = formatTime(video.duration);

  if (isUsablePosition(video.duration) && video.duration > 0) {
    seekBar.value = (video.currentTime / video.duration) * 100;
  }
});

// --- Fullscreen -------------------------------------------------------------

function isTouchDevice() {
  return window.matchMedia("(pointer: coarse)").matches;
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
  isFullscreen = inFullscreen();

  fullscreenBtn.innerHTML = isFullscreen ? "&#10005;" : "&#9974;";
  fullscreenBtn.title = isFullscreen ? "Exit fullscreen" : "Fullscreen";
  fullscreenBtn.setAttribute("aria-label", fullscreenBtn.title);

  if (!isFullscreen) {
    unlockOrientation();
    setControlsVisible(true); // never leave it hidden outside fullscreen
    return;
  }

  const locked = await lockLandscape();
  if (!locked && isTouchDevice()) showToast("Rotate your device for the best view.");

  // Re-arm the idle timer on every fullscreen transition so entering fullscreen
  // always starts a fresh countdown rather than inheriting a stale one.
  setControlsVisible(true);
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

// Registered once on document — these events all bubble from the element up to
// document, and window additionally sees them, so a single handler is enough.
document.addEventListener("fullscreenchange", syncFullscreenState);
document.addEventListener("webkitfullscreenchange", syncFullscreenState);
video.addEventListener("webkitbeginfullscreen", syncFullscreenState);
video.addEventListener("webkitendfullscreen", syncFullscreenState);

video.addEventListener("ended", async () => {
  if (!currentMovieId) return;
  resetPlaybackRate();
  try {
    await invokeTimed("Advance", 0, creds.roomId, { movieId: currentMovieId, position: 0 });
  } catch (err) {
    console.warn("Advance request failed:", err);
  }
});

// --- Chat -------------------------------------------------------------------

// The server hands back the display name it stored, which is this one trimmed
// with a blank name replaced by "Guest". Comparing against the same
// normalisation keeps a stray space from making you toast your own messages.
function ownDisplayName() {
  const name = (creds.displayName || "").trim();
  return name === "" ? "Guest" : name;
}

// A nudge that someone said something while you were watching rather than
// reading. Skipped when the panel is already on screen and caught up, when the
// tab is in the background, and for our own messages — you typed those.
// Matched on display name because the broadcast carries no sender id and a room
// only holds two people.
function announceChatMessage(msg) {
  if (!msg) return;
  if ((msg.displayName || "Guest") === ownDisplayName()) return;
  if (document.hidden || chatIsCaughtUp()) return;

  showToast(`${msg.displayName || "Guest"} sent a message`);
}

// On screen and already scrolled to the newest message means nothing was missed
// by keeping your eyes on the video.
function chatIsCaughtUp() {
  const rect = chatBox.getBoundingClientRect();
  const onScreen = rect.top < window.innerHeight && rect.bottom > 0;
  if (!onScreen) return false;

  return chatBox.scrollTop + chatBox.clientHeight >= chatBox.scrollHeight - 24;
}

// Only a drop is announced — the Hub sends this event without a payload on
// join/reconnect, where there is nothing new to say.
function announcePresenceChange(change) {
  if (!change || !change.displayName) return;
  if (change.displayName === ownDisplayName()) return;
  if (change.status !== "Offline") return;

  showToast(`${change.displayName} went offline`);
}

// The same message can arrive twice — once from the history replay and once from
// the live broadcast, if it was sent while the Hub was answering Join. The server
// gives every message an id, so the second copy is dropped.
function renderChatMessage(msg) {
  if (!msg) return;

  if (msg.id) {
    if (seenChatMessageIds.has(msg.id)) return;
    seenChatMessageIds.add(msg.id);
  }

  appendChatMessage(msg.displayName, msg.message);
}

function appendChatMessage(displayName, message) {
  // Built with textContent, not innerHTML — both displayName and message are
  // user-typed and broadcast to the other participant, so neither should be
  // interpreted as HTML (that's how a <script> in a chat message would run).
  const row = document.createElement("div");
  row.className = "chat-msg";

  const nameEl = document.createElement("b");
  nameEl.textContent = displayName || "Guest";

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
  if (!isConnected()) {
    showToast("Not connected — message not sent.");
    return;
  }

  chatInput.value = "";
  try {
    await connection.invoke("SendMessage", creds.roomId, creds.displayName || "Guest", text);
  } catch (err) {
    showToast("Message failed to send.");
  }
}

chatSendBtn.addEventListener("click", sendChatMessage);
chatInput.addEventListener("keydown", e => {
  if (e.key === "Enter" && !e.shiftKey) {
    e.preventDefault();
    sendChatMessage();
  }
});

// --- Invite copy ------------------------------------------------------------

copyInviteBtn.addEventListener("click", async () => {
  try {
    await navigator.clipboard.writeText(roomCode);
    showToast("Room code copied");
  } catch {
    showToast(`Room code: ${roomCode}`);
  }
});

// --- Close room (host only) -------------------------------------------------

closeRoomBtn.addEventListener("click", async () => {
  const confirmed = confirm("Close this room? This deletes it and its uploaded movies for both of you — this can't be undone.");
  if (!confirmed) return;

  try {
    await SyncWatchAPI.closeRoom(creds.roomId, creds.token);
    leavingIntentionally = true;
    localStorage.removeItem(`syncwatch:${roomCode}`);
    window.location.replace("index.html");
  } catch (err) {
    showToast(err.message || "Could not close the room.");
  }
});

// --- Boot -------------------------------------------------------------------

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