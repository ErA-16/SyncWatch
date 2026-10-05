// myrooms.js — reads every "syncwatch:<code>" key this browser has stored.
// There's no backend endpoint for "list my rooms" because SyncWatch has no
// accounts at all (a deliberate v1 decision) — this list only exists because
// the browser itself remembered these credentials, nothing is tracked server-side.

const listEl = document.getElementById("room-list");
const toastEl = document.getElementById("toast");

function showToast(message) {
  toastEl.textContent = message;
  toastEl.classList.add("show");
  setTimeout(() => toastEl.classList.remove("show"), 2200);
}

function getStoredRooms() {
  const rooms = [];
  for (let i = 0; i < localStorage.length; i++) {
    const key = localStorage.key(i);
    if (key && key.startsWith("syncwatch:")) {
      const code = key.slice("syncwatch:".length);
      const data = JSON.parse(localStorage.getItem(key));
      rooms.push({ code, ...data });
    }
  }
  return rooms;
}

function render() {
  const rooms = getStoredRooms();

  if (rooms.length === 0) {
    listEl.innerHTML = `<div class="card"><p style="margin:0;">No rooms remembered on this device yet.</p></div>`;
    return;
  }

  listEl.innerHTML = "";

  rooms.forEach(room => {
    const card = document.createElement("div");
    card.className = "card";

    const top = document.createElement("div");
    top.style.cssText = "display:flex; justify-content:space-between; align-items:center; margin-bottom:10px;";

    const title = document.createElement("div");
    const codeEl = document.createElement("b");
    codeEl.style.color = "var(--accent)";
    codeEl.textContent = room.code;
    const roleEl = document.createElement("span");
    roleEl.style.cssText = "color:var(--text-dim); font-size:0.8rem; margin-left:8px;";
    roleEl.textContent = room.isHost ? "you're the host" : "joined as guest";
    title.append(codeEl, roleEl);

    top.appendChild(title);
    card.appendChild(top);

    const row = document.createElement("div");
    row.style.cssText = "display:flex; gap:8px;";

    const openBtn = document.createElement("button");
    openBtn.className = "primary";
    openBtn.textContent = "Open";
    openBtn.addEventListener("click", () => {
      window.location.href = `room.html?code=${room.code}`;
    });
    row.appendChild(openBtn);

    if (room.isHost) {
      const deleteBtn = document.createElement("button");
      deleteBtn.className = "secondary";
      deleteBtn.style.color = "var(--danger)";
      deleteBtn.style.borderColor = "var(--danger)";
      deleteBtn.textContent = "Delete";
      deleteBtn.addEventListener("click", async () => {
        if (!confirm(`Close room ${room.code}? This deletes it and its movies for everyone — can't be undone.`)) return;
        try {
          await SyncWatchAPI.closeRoom(room.roomId, room.token);
          localStorage.removeItem(`syncwatch:${room.code}`);
          render();
        } catch (err) {
          showToast(err.message || "Could not delete — it may already be gone.");
        }
      });
      row.appendChild(deleteBtn);
    } else {
      const forgetBtn = document.createElement("button");
      forgetBtn.className = "secondary";
      forgetBtn.textContent = "Forget";
      forgetBtn.title = "Just removes it from this list — doesn't close the room (only the host can)";
      forgetBtn.addEventListener("click", () => {
        localStorage.removeItem(`syncwatch:${room.code}`);
        render();
      });
      row.appendChild(forgetBtn);
    }

    card.appendChild(row);
    listEl.appendChild(card);
  });
}

render();
