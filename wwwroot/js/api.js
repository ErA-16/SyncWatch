// api.js — thin wrappers around the SyncWatch backend.
// Assumes this frontend is served from the same origin as the API
// (e.g. dropped into the ASP.NET Core project's wwwroot folder).
// If served separately, set API_BASE to the full backend URL and enable CORS server-side.

const API_BASE = "";

const SyncWatchAPI = {
  async createRoom(hostName) {
    const res = await fetch(`${API_BASE}/api/Room/create-room`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ hostName })
    });
    if (!res.ok) throw new Error(await res.text() || "Could not create room.");
    return res.json(); // { roomId, roomCode, participantId, token }
  },

  async joinRoom(roomCode, displayName) {
    const res = await fetch(`${API_BASE}/api/Room/join-room`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ roomCode, displayName: displayName || null })
    });
    if (!res.ok) throw new Error(await res.text() || "Could not join room.");
    return res.json(); // { roomId, participantId, token }
  },

  async getRoom(code) {
    const res = await fetch(`${API_BASE}/api/Room/${code}`);
    if (!res.ok) throw new Error(await res.text() || "Room not found.");
    return res.json(); // { roomId, code, status, participants[], movies[] }
  },

  async uploadMovie(roomId, file) {
    const form = new FormData();
    form.append("file", file);
    const res = await fetch(`${API_BASE}/api/Movie/upload/${roomId}`, {
      method: "POST",
      body: form
    });
    if (!res.ok) throw new Error(await res.text() || `Failed to upload ${file.name}.`);
    return res.json(); // { id, title, episodeNumber }
  },

  streamUrl(movieId) {
    return `${API_BASE}/api/Movie/stream/${movieId}`;
  },

  async leaveRoom(token) {
    await fetch(`${API_BASE}/api/Room/leave`, {
      method: "PATCH",
      headers: { token },
      keepalive: true
    });
  },

  async closeRoom(roomId, hostToken) {
    const res = await fetch(`${API_BASE}/api/Room/close-room/${roomId}`, {
      method: "DELETE",
      headers: { hostToken }
    });
    if (!res.ok && res.status !== 204) throw new Error(await res.text() || "Could not close room.");
  },

  async reconnectRoom(token) {
    await fetch(`${API_BASE}/api/Room/reconnect`, {
      method: "PATCH",
      headers: { token }
    });
  }
};

// Per-room credentials, kept in localStorage so a refresh doesn't lose identity.
const SyncWatchStorage = {
  save(code, data) {
    localStorage.setItem(`syncwatch:${code}`, JSON.stringify(data));
  },
  load(code) {
    const raw = localStorage.getItem(`syncwatch:${code}`);
    return raw ? JSON.parse(raw) : null;
  }
};
