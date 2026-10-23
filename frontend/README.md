# SportStats frontend

An original React/TypeScript interface for the existing read-only SportStats API.

## Local development

Use Node.js 22.12 or later. Run `npm ci` then `npm run dev` from this directory.
Run `npm run typecheck`, `npm run lint`, `npm run build`, and `npm test` to verify.

From the isolated worktree root, start on a fixed local port with:

```powershell
cd frontend
npm run dev -- --port 5173 --strictPort
```

Open http://127.0.0.1:5173/. Ensure Node.js and npm are available on PATH.

Favorites store only match IDs in this browser's localStorage under
`sportstats.favorites.v1`. Invalid saved values are ignored; when storage is
blocked or full, choices work in memory for the current session. Saved matches
still respect the selected date, source, filters and loaded API page. Favorites
do not synchronize between open tabs. No external fonts or analytics are loaded.

Verification includes headless Edge at 1440, 768 and 390px, keyboard disclosure
and skip-link focus, basic accessibility-tree semantics, and mocked API failure
states. Manual screen-reader testing and other browsers remain unverified.

Demo mode uses fictional deterministic fixtures; it does not contact the API.
API mode reads `/api` on the same origin. A separately authorized environment
must serve or proxy those routes to the existing backend. Vite does not start
the backend, configure PostgreSQL, or provide a database connection. API errors
remain errors and never silently substitute demo content.

Match date boundaries use UTC. Missing scores remain unavailable, not zero.
The existing API exposes persisted snapshots, not a guaranteed live feed.
Search and status filters apply to the loaded match page; pagination remains
explicit. Unsupported statistics and future views are labeled as unavailable.

Production hosting needs an SPA fallback to `index.html` for client routes and
same-origin routing for `/api`. Hosting and deployment are outside this change.
