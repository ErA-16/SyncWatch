// api.js — thin wrappers around the SyncWatch backend.
// Assumes this frontend is served from the same origin as the API
// (e.g. dropped into the ASP.NET Core project's wwwroot folder).
// If served separately, set API_BASE to the full backend URL and enable CORS server-side.

const API_BASE = "";

async function readError(res, fallback) {
  // The API returns bare strings from BadRequest()/NotFound(), so read as text
  // first — calling res.json() on a plain-text body throws a SyntaxError that
  // hides the actual server message from the user.
  const body = await res.text().catch(() => "");
  if (!body) return fallback;

  try {
    const parsed = JSON.parse(body);
    if (typeof parsed === "string") return parsed;
    return parsed.title || parsed.error || parsed.message || fallback;
  } catch {
    return body;
  }
}

async function readJson(res, fallback) {
  const body = await res.text().catch(() => "");
  if (!body) return fallback;
  try {
    return JSON.parse(body);
  } catch {
    throw new Error(fallback);
  }
}

const SyncWatchAPI = {
  async createRoom(hostName) {
    const res = await fetch(`${API_BASE}/api/Room/create-room`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ hostName })
    });
    if (!res.ok) throw new Error(await readError(res, "Could not create room."));
    return readJson(res, null); // { roomId, roomCode, participantId, token }
  },

  async joinRoom(roomCode, displayName) {
    const res = await fetch(`${API_BASE}/api/Room/join-room`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ roomCode, displayName: displayName || null })
    });
    if (!res.ok) throw new Error(await readError(res, "Could not join room."));
    return readJson(res, null); // { roomId, participantId, token }
  },

  async getRoom(code) {
    const res = await fetch(`${API_BASE}/api/Room/${encodeURIComponent(code)}`);
    if (!res.ok) throw new Error(await readError(res, "Room not found."));
    return readJson(res, null); // { roomId, code, status, participants[], movies[] }
  },

  async uploadMovie(roomId, file, onProgress) {
    // The bytes never touch this API. Cloudflare caps request bodies at 100 MB, so
    // a 300 MB movie POSTed here dies with a 502 before ASP.NET sees it. The
    // browser PUTs straight to R2 using a short-lived presigned URL instead.
    const ticket = await (async () => {
      const res = await fetch(`${API_BASE}/api/Movie/upload-ticket/${roomId}`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          fileName: file.name,
          fileSize: file.size,
          contentType: file.type || null
        })
      });
      if (!res.ok) throw new Error(await readError(res, `Rejected ${file.name}.`));
      return readJson(res, null);
    })();

    await putToR2(ticket, file, onProgress);

    // The server can't sniff the container off a presigned upload, so read the
    // duration here while the file is still local to the browser.
    const durationSeconds = await readVideoDuration(file);

    const res = await fetch(`${API_BASE}/api/Movie/upload-complete/${roomId}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        objectKey: ticket.objectKey,
        fileName: file.name,
        fileSize: file.size,
        contentType: ticket.contentType,
        durationSeconds
      })
    });
    if (!res.ok) throw new Error(await readError(res, `Failed to finish uploading ${file.name}.`));
    return readJson(res, null); // { id, title, episodeNumber }
  },

  streamUrl(movieId) {
    return `${API_BASE}/api/Movie/stream/${movieId}`;
  },

  async leaveRoom(token) {
    // keepalive lets this survive the page unload that pagehide is riding on.
    await fetch(`${API_BASE}/api/Room/leave`, {
      method: "PATCH",
      headers: { token },
      keepalive: true
    }).catch(() => {});
  },

  async closeRoom(roomId, hostToken) {
    const res = await fetch(`${API_BASE}/api/Room/close-room/${roomId}`, {
      method: "DELETE",
      headers: { hostToken }
    });
    if (!res.ok && res.status !== 204) {
      throw new Error(await readError(res, "Could not close room."));
    }
  },

  async reconnectRoom(token) {
    await fetch(`${API_BASE}/api/Room/reconnect`, {
      method: "PATCH",
      headers: { token }
    }).catch(() => {});
  }
};

// Upload straight to R2 with a presigned URL. XMLHttpRequest rather than fetch
// because we need upload progress events, and fetch still can't report them.
function putToR2(ticket, file, onProgress) {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open("PUT", ticket.uploadUrl, true);

    // Content-Type was signed into the URL — sending a different one gets the
    // whole upload rejected with SignatureDoesNotMatch.
    xhr.setRequestHeader("Content-Type", ticket.contentType);

    xhr.upload.onprogress = (e) => {
      if (onProgress && e.lengthComputable) onProgress(e.loaded / e.total);
    };
    xhr.onload = () => {
      if (xhr.status >= 200 && xhr.status < 300) resolve();
      else reject(new Error(readXhrError(xhr, file)));
    };
    xhr.onerror = () => reject(new Error(`Lost connection while uploading ${file.name}.`));
    xhr.onabort = () => reject(new Error("Upload cancelled."));
    xhr.ontimeout = () => reject(new Error(`Upload of ${file.name} timed out.`));

    xhr.send(file);
  });
}

function readXhrError(xhr, file) {
  // R2 answers a failed PUT with an XML body containing <Code>.
  const code = /<Code>([^<]+)<\/Code>/.exec(xhr.responseText || "");
  if (code) {
    if (code[1] === "AccessDenied") return "Storage refused the upload. Try again.";
    if (code[1] === "SignatureDoesNotMatch") return "That upload link had already expired. Try again.";
    return `Storage refused the upload (${code[1]}).`;
  }
  if (xhr.status === 0) return `Lost connection while uploading ${file.name}.`;
  return `Failed to upload ${file.name} (storage error ${xhr.status}).`;
}

// Duration for the room list. Resolves to null rather than rejecting: an
// unreadable duration is cosmetic, and it's never worth failing an upload over.
function readVideoDuration(file) {
  return new Promise((resolve) => {
    const url = URL.createObjectURL(file);
    const video = document.createElement("video");
    let settled = false;

    const finish = (value) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      video.removeAttribute("src");
      video.load();
      URL.revokeObjectURL(url);
      resolve(value);
    };

    // Some files never fire loadedmetadata; don't hang the upload waiting.
    const timer = setTimeout(() => finish(null), 10000);

    video.preload = "metadata";
    video.muted = true;
    video.onloadedmetadata = () => {
      const seconds = video.duration;
      finish(Number.isFinite(seconds) && seconds > 0 ? seconds : null);
    };
    video.onerror = () => finish(null);
    video.src = url;
  });
}

// Per-room credentials, kept in localStorage so a refresh doesn't lose identity.
const SyncWatchStorage = {
  save(code, data) {
    localStorage.setItem(`syncwatch:${code}`, JSON.stringify(data));
  },
  load(code) {
    try {
      const raw = localStorage.getItem(`syncwatch:${code}`);
      return raw ? JSON.parse(raw) : null;
    } catch {
      // Corrupted entry — drop it so the user lands back on index.html cleanly
      // instead of crashing on every visit.
      localStorage.removeItem(`syncwatch:${code}`);
      return null;
    }
  },
  remove(code) {
    localStorage.removeItem(`syncwatch:${code}`);
  }
};