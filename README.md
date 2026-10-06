# SyncWatch 🎬 *A private room where friends watch the same movie, in sync, from wherever they are.* 

---

## The idea
I didn't want Netflix Party. I didn't want a browser extension or a third-party service sitting between me and a friend trying to watch something together. I wanted to build the thing myself — mostly because the actual problem underneath "watch a movie together" turned out to be way more interesting than it sounds. 

It's not a video player. It's a distributed systems problem wearing a video player as a costume.

## The real problem
Two browsers, two independent video elements, no shared clock. One person presses pause. The other person's video needs to know, in real time, without either side trusting the other blindly. 

That's the whole project, honestly. Everything else — rooms, uploads, chat — is scaffolding around one question: **how do you keep two clients honest about where they are, when neither of them is in charge?**

The answer I landed on: the server is the only source of truth. Every play, pause, or seek is just a request — the server decides what actually happened, stamps it with a version number, and broadcasts it out. Clients don't negotiate with each other. They just listen.

## What it actually does
- 🔗 **No accounts.** A room code *is* the access control. Create a room, share the code, done.
- ▶️ **Real sync.** Play, pause, seek, switch episodes — reflected on the other side in real time, with drift correction running quietly in the background so nobody notices the seams.
- 💬 **A small chat panel**, because watching something together felt incomplete without being able to react to it.
- 📱 **Works properly on a phone**, not just as an afterthought — I rebuilt the mobile layout more than once until it actually felt right to use.
- 🗑️ **Rooms clean up after themselves** — nothing lingers on a server past a few days of being forgotten.
- 🌐 **Live Demo:** https://syncwatch-0bmz.onrender.com

## How it's built

| Layer | What's doing the work |
|---|---|
| Backend | ASP.NET Core 10 — layered properly: models → interfaces → services → controllers, each piece tested on its own |
| Real-time | SignalR — room-scoped groups, server-authoritative conflict resolution |
| Database | PostgreSQL, hosted on Neon |
| Storage | Cloudflare R2 — movies never touch the app server's own disk |
| Frontend | Plain HTML, CSS, JS. No framework. I wanted to see what the sync logic actually looked like with nothing hiding it from me |
| Hosted on | Render, via Docker |

## Building this
I started this scared of it, honestly — I'd never finished something this size, and the real-time sync part made no sense to me the first few times I tried to learn it from tutorials. 

What actually worked was slowing down and designing every piece on paper before writing a line of code: deciding what happens when two people click play at the same moment, what a reconnect should look like, what the server owes the client versus what the client has to just trust. Most of the bugs that showed up later weren't really coding mistakes — they were decisions I hadn't made yet, surfacing as exceptions. Once the design was actually settled, the code mostly just confirmed it.

## Running it locally
```bash
git clone https://github.com/<your-username>/syncwatch.git
cd syncwatch
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "<your Postgres connection string>"
dotnet user-secrets set "R2:AccessKey" "<your R2 access key>"
dotnet user-secrets set "R2:SecretKey" "<your R2 secret key>"
dotnet user-secrets set "R2:Endpoint" "<your R2 endpoint>"
dotnet user-secrets set "R2:BucketName" "<your R2 bucket>"
dotnet ef database update
dotnet run
```

---
<sub>Built by [Taiwo](https://github.com). Live App: https://syncwatch-0bmz.onrender.com</sub>
