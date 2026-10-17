# FE: Cupboard Tracker web app

The phone-first React app (React 19 + TypeScript, Vite 6, Vitest + Testing Library, ESLint, Prettier), hosted on
Azure Static Web Apps (Free) and deployed by `deploy/ui.sh`. Users sign in with Entra ID (MSAL, redirect flow) and
the app calls the API Function app directly with a bearer token.

```bash
cd FE
npm ci
cp .env.example .env.local   # fill in from deploy/entra.sh's outputs and the API hostname (not secrets)
npm run dev                  # http://localhost:5173 (registered as an SPA redirect URI; the API allows it in CORS)
npm test && npm run lint && npm run build
```

- `src/tokens.css`: design tokens from the mockups (colours, radii, touch target, Figtree). Use them, not raw values.
- `src/api.ts`: the only way to call the API (bearer token; 401 → sign in again and come back; 403 → no access).
- `public/staticwebapp.config.json`: SPA fallback routing. SWA's built-in auth isn't used.
