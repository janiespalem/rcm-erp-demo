# FactoryFlow ERP Demo Frontend

Vue 3 + Vite frontend for the FactoryFlow ERP Demo.

The app is built as a role-aware internal tool UI:

- PIN login by role;
- tab navigation based on role permissions;
- order list and order workspace;
- quote, materials, catalog, analytics, and production views;
- shared state through composables instead of a global store library.

## Development

```bash
npm ci
npm run dev
```

The dev server proxies `/api` to the FastAPI backend.

## Build

```bash
npm run build
```

The backend serves the built files from `frontend-vite/dist/`.
