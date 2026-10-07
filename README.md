# FactoryFlow

A native Windows ERP demonstration built with **C# / .NET 10, WPF, ASP.NET Core and PostgreSQL**. Explore customer follow-up, orders, pricing, documents, catalogs, production contracts, steel deliveries and shift reports using entirely fictional data.

FactoryFlow is a public demonstration, not an employee deployment. Demo prices, customers, accounts, attachments and operational records are synthetic. The Windows interface uses Polish business terminology.

## Quick start

Install Docker Desktop (or Docker Engine with Compose). Clone this repository and run:

```sh
docker compose up -d --wait db
docker compose --profile setup run --build --rm bootstrap
docker compose up -d --build --wait api
```

The API listens at `http://127.0.0.1:18081`; check `http://127.0.0.1:18081/health`. PostgreSQL has no published port. Bootstrap is an explicit, repeatable step: it applies migrations, configures restricted runtime logins and native writer ownership, then creates synthetic fixtures through the versioned API. Normal API startup does not migrate or seed.

On Windows, install the self-contained **FactoryFlow-Setup.exe** from this repository's release assets. No .NET SDK or Python installation is needed by the person trying the desktop. Run the API on the same Windows machine, then open FactoryFlow and sign in. FactoryFlow installs as `FactoryFlow.exe` under `%LOCALAPPDATA%\FactoryFlow`; its protected saved login, calculator state and single-instance mutex are separate from RCM. Startup, background and manual updates are disabled. The client ignores `RCM_*` configuration. An optional `FACTORYFLOW_SERVER_URL` must be a loopback HTTP(S) root URL; redirects and HTTP proxies are disabled.

The installer is produced and tested by the Windows workflow. Until a release asset is published, build it using [the Windows workflow](.github/workflows/windows.yml) and `scripts/package-desktop.ps1` (Windows, .NET SDK from `dotnet/global.json`, and `vpk` 1.2.158); do not substitute an employee ERP installer. Public Windows CI installs FactoryFlow beside a synthetic RCM package. The same test accepts `-ExistingRcmSetupPath` for private acceptance beside the actual RCM installer, using a separate local server sink and verifying preserved identity files. Every package includes `release.json` with its public source commit, version and installation identity; `verification.json` binds the installer and coexistence test by SHA-256. Linux backend verification does not establish Windows acceptance.

## Demo accounts

All accounts use password **`FactoryFlow-Demo-2026!`**. These credentials are deliberately public and suitable only for this local synthetic demo.

| Username | Role | Useful scenario |
|---|---|---|
| `office` | Biuro; North CRM; assigned reviewer | Orders, documents, customer follow-up, report acceptance |
| `engineer` | Technolog; North CRM | Pricing, catalog editing and shift reports |
| `production` | Produkcja | Draft, save and finalize a shift report |
| `director` | CEO | Read operational reports and analytics |
| `sales.north` | CRM only; North team | Browse North customers, a recorded contact and contact plans |
| `sales.south` | CRM only; South team | Browse independent South customers; North records are inaccessible |

Try these workflows:

1. Sign in as `sales.north`, edit a topic and schedule a contact. Sign in as `sales.south` to see a separate customer set.
2. Sign in as `office`, open a demo order and download its native PDF. The sample PDF attachment was generated from fictional records during bootstrap.
3. Sign in as `engineer`, inspect three synthetic materials and three operations, open the populated DEMO-01 project/SOP and price an order using synthetic rates.
4. Open the synthetic production contract and its steel delivery. Masses are integer grams; theoretical coverage is not physical production or dispatch availability.
5. As `office`, inspect the finalized report linked to the contract and accept it. As `production`, create another report and practice draft/finalization handling.
6. Keep an edited form open during a conflicting change. The client should retain input and require an explicit conflict decision.

Data and attachments survive `docker compose stop` and `docker compose up -d`. To stop the demo, run `docker compose down`. Adding `-v` deletes the demo volumes and all records you entered; use it only for an intentional fresh start.

## Architecture

```text
FactoryFlow WPF desktop → /api/v1 → ASP.NET Core → PostgreSQL
                                     │
                              native PDF documents

Explicit bootstrap → Alembic: public
                   → EF: crm, production
                   → restricted runtime grants + synthetic fixtures
```

- WPF UI and CommunityToolkit.Mvvm provide the Windows shell and forms.
- Versioned commands live in `dotnet/Rcm.Contracts`; the desktop communicates only over HTTP.
- Native identity, orders, catalog and shift-report writers are explicitly enabled. The demo does not require FastAPI for authentication or business writes.
- Alembic remains the owner of `public`. EF owns `crm` and `production`, with separate migration history tables. Runtime database roles cannot migrate the schema.
- CRM commands combine expected versions, request IDs, audit changes and persisted receipts. Team membership is resolved on the server.
- Identity, orders, catalogs, shift reports, CRM and production use separate restricted database logins. Public demo configuration contains only disposable demo credentials.
- API bindings are loopback-only by default. The legacy HTTP adapter points to an unavailable loopback endpoint so an accidental fallback fails locally.

The native source was transferred using explicit per-file manifests with source hashes. No private Git history, production database, infrastructure configuration, real credentials or uploaded business documents are included. Public display identity is FactoryFlow; shared C# namespaces remain stable, while the desktop executable, installation and saved-login identities are independent.

## Reproducible engineering checks

With the demo running, use Python 3 to exercise the real HTTP interface (standard library only):

```sh
python demo/verify.py
docker compose restart api
python demo/verify.py --after-restart
```

The verification checks all six account logins, CRM team isolation, eight competing retries of the same customer/order creation, receipt replay after a discarded response, stale-edit conflicts, native PDF generation, report acceptance and persistence after restart. These are correctness checks, not throughput benchmarks.

LEGO demonstration prices use the existing public synthetic price table, with repriced monetary expectations and unchanged layout/geometry fixtures. Pricing fixtures are hand-built arithmetic edge cases (zero, missing values, rounding and fractional sums), not sampled customer quotes.

Calculation regression tests run from `dotnet/` with the pinned SDK:

```sh
dotnet test Rcm.Calculators.Tests -c Release
dotnet test Rcm.Orders.Tests -c Release
```

Linux CI bootstraps a new PostgreSQL volume, exercises native workflows and repeats bootstrap without resetting entered data. Windows CI builds/tests WPF and the installer. Desktop form retention, update restart behavior, keyboard access and display scaling require the Windows scenarios; a Linux build cannot validate them. No performance figures are claimed without measured hardware, dataset and command.

## Legacy web profile

The earlier Vue/FastAPI demonstration is retained under `legacy/` with its own SQLite volume and local port. It is an independent historical slice, not a second writer for the native database:

```sh
docker compose --profile legacy up -d --build legacy
```

Open `http://127.0.0.1:18080`. Legacy PINs: Biuro `1111`, Technolog `2222`, CEO `3333`, production shifts I/II `4444`/`5555`. Earlier web screenshots in `docs/screenshots/` belong to this legacy client; they are not native WPF screenshots. The legacy source and fixtures remain separately runnable. Native screenshots are added only after capture on Windows against the synthetic demo.

## Limits

This is a working pilot demonstration. It is not a production deployment guide, accounting system or confirmed manufacturing specification. Catalog norms require domain confirmation; theoretical material coverage is distinct from recorded consumption. Quote editing retains the source module's documented versioning limits. Public demo passwords and connection strings must never be reused for real data. New production permissions and user data are outside this demo's scope.
