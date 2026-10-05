// create.js — logic for index.html

const fileInput = document.getElementById("movie-files");
const fileDrop = document.getElementById("file-drop");
const fileDropLabel = document.getElementById("file-drop-label");
const fileListEl = document.getElementById("file-list");

let selectedFiles = [];

fileDrop.addEventListener("click", () => fileInput.click());

fileInput.addEventListener("change", () => {
  selectedFiles = Array.from(fileInput.files);
  renderFileList();
});

function renderFileList() {
  if (selectedFiles.length === 0) {
    fileDropLabel.textContent = "Tap to choose one or more files";
    fileListEl.innerHTML = "";
    return;
  }

  const totalMB = (selectedFiles.reduce((sum, f) => sum + f.size, 0) / (1024 * 1024)).toFixed(1);
  fileDropLabel.textContent = `${selectedFiles.length} file${selectedFiles.length > 1 ? "s" : ""} selected — ${totalMB} MB`;

  fileListEl.innerHTML = "";
  selectedFiles.forEach((f, index) => {
    const row = document.createElement("div");

    const nameSpan = document.createElement("span");
    nameSpan.textContent = f.name; // textContent — never trust a filename as HTML

    const sizeSpan = document.createElement("span");
    sizeSpan.className = "ep";
    sizeSpan.textContent = `${(f.size / (1024 * 1024)).toFixed(1)} MB`;

    const removeBtn = document.createElement("button");
    removeBtn.textContent = "✕";
    removeBtn.type = "button";
    removeBtn.className = "remove-file-btn";
    removeBtn.addEventListener("click", (e) => {
      e.stopPropagation(); // don't let the click bubble up and reopen the file picker
      selectedFiles.splice(index, 1);
      renderFileList();
    });

    row.append(nameSpan, sizeSpan, removeBtn);
    fileListEl.appendChild(row);
  });
}

// --- Create room ---

const createBtn = document.getElementById("create-btn");
const createStatus = document.getElementById("create-status");

createBtn.addEventListener("click", async () => {
  const hostName = document.getElementById("host-name").value.trim();

  if (!hostName) {
    setStatus(createStatus, "Enter your name first.", "error");
    return;
  }
  if (selectedFiles.length === 0) {
    setStatus(createStatus, "Choose at least one movie file.", "error");
    return;
  }

  createBtn.disabled = true;

  try {
    setStatus(createStatus, "Creating room…", "");
    const room = await SyncWatchAPI.createRoom(hostName);

    SyncWatchStorage.save(room.roomCode, {
      roomId: room.roomId,
      participantId: room.participantId,
      token: room.token,
      displayName: hostName,
      isHost: true
    });

    // Sequential on purpose: uploads are large and parallel ones would saturate
    // the connection for no real gain, and the progress readout stays honest.
    for (let i = 0; i < selectedFiles.length; i++) {
      setStatus(createStatus, `Uploading ${i + 1} of ${selectedFiles.length}…`, "");
      await SyncWatchAPI.uploadMovie(room.roomId, selectedFiles[i]);
    }

    setStatus(createStatus, "Room ready — taking you in…", "ok");
    window.location.href = `room.html?code=${room.roomCode}`;
  } catch (err) {
    setStatus(createStatus, err.message, "error");
    createBtn.disabled = false;
  }
});

// --- Join room ---

const joinBtn = document.getElementById("join-btn");
const joinStatus = document.getElementById("join-status");

joinBtn.addEventListener("click", async () => {
  const code = document.getElementById("join-code").value.trim().toUpperCase();
  const name = document.getElementById("join-name").value.trim();

  if (!code) {
    setStatus(joinStatus, "Enter a room code.", "error");
    return;
  }

  joinBtn.disabled = true;

  try {
    setStatus(joinStatus, "Joining…", "");
    const result = await SyncWatchAPI.joinRoom(code, name);

    SyncWatchStorage.save(code, {
      roomId: result.roomId,
      participantId: result.participantId,
      token: result.token,
      displayName: name || null,
      isHost: false
    });

    window.location.replace(`room.html?code=${code}`);
  } catch (err) {
    setStatus(joinStatus, err.message, "error");
    joinBtn.disabled = false;
  }
});

function setStatus(el, text, kind) {
  el.textContent = text;
  el.className = "status-line" + (kind ? ` ${kind}` : "");
}
