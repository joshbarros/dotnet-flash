# dotnet-flash

Flash sales where the cart cannot hoard the stock. The scarce number is the **pieces left** in a deal, and the **Deals** module is its only writer. A shopper can **hold pieces and pay later**: the pieces leave the shelf at once, but the hold carries an expiry, no receipt is issued while it is pending, and a sweeper in the Deals module gives the pieces back (and the shopper's allowance, and tells the order) when the time runs out. Confirming and expiring race for the same row: both are a single conditional `UPDATE` on the hold, so exactly one wins, with several API instances sweeping at once (`FOR UPDATE SKIP LOCKED`) and with a confirm landing on the very millisecond of the deadline. Two further rules hold in the same transactions as before: a shopper may hold at most **`MaxPerShopper`** pieces of a deal (an atomic upsert), and the pieces left are taken with one conditional `UPDATE`, so when two shoppers fight for the last piece one commits a hold and the other is told the deal is short. A reference that contains the poison token `POISON` is committed as rejected by the Receipts module, the message is dead-lettered, and a compensating command gives the pieces back. A later refund does the same, but it does not roll back the receipt that was already issued.

One ASP.NET Core process, one Postgres database. An HTTP write saves the order and an outbox row in one local transaction; a `BackgroundService` relay publishes outbox rows to RabbitMQ (the work queue) and Kafka (the event log); the next module consumes from RabbitMQ, claims an inbox key in the same transaction as its own write, and writes its own row and outbox row. Saga steps never call each other in-process. Delivery is at-least-once: the relay can publish and crash before it marks a row sent, so the row goes out again, and every consumer dedupes with its inbox key. Each saga step only moves an order forward from the status it expects.

Everything behind the saga is signed in: **ASP.NET Core Identity** with **JWT access tokens and rotating refresh tokens** (see [Authentication and authorization](#authentication-and-authorization)). Customers see and refund only their own orders; staff see everything and open deals.

> The demo credentials in `compose.yaml` and `appsettings.Development.json` are for local use only. Set `JWT_KEY` and `ADMIN_PASSWORD` (see `.env.example`) before putting this anywhere shared. Compose binds every port to `127.0.0.1`.

![The Flash UI signed in as the administrator: a deal with one order sold, one held with a pay-by time and a Confirm button, and one expired](docs/screenshot.png)

*The UI reads everything from the API; shown signed in as the seeded administrator. The first order was held and confirmed, the second is held and still has a pay-by time, the third was never confirmed and expired, so its pieces are back on sale.*

## Order statuses

```mermaid
flowchart LR
    Requested -->|pieces held| Held
    Requested -->|short or over the limit: no hold written| Rejected
    Held -->|receipt issued| Sold
    Held -->|pending hold: confirmed in time| Held
    Held -->|pending hold: not confirmed in time| Expired
    Held -->|poison reference: receipt rejected| Rejected
    Held -->|refund| Refunded
    Sold -->|refund| Refunded
```

`Refunded` and `Rejected` (after a hold) put the pieces back through a `ReleasePieces` command, and Deals releases a hold at most once (an expired hold is released the same way, by the sweeper, and moves the order to `Expired`), and gives the shopper their allowance back with it. The receipt has its own status (`Issued` or `Rejected`) and a refund never changes it. If the refund gets there before the final mark, the late mark finds the order is no longer `Held` and does nothing.

## How the modules talk

```mermaid
sequenceDiagram
    participant C as Client
    participant O as Orders
    participant T as Deals
    participant E as Receipts

    C->>O: POST /api/orders
    Note over O: order + outbox row, one transaction
    O-->>C: 202 Requested
    O->>T: pieces.reserve (relay → RabbitMQ, copy to Kafka)
    Note over T: inbox claim, per-shopper limit upsert, conditional UPDATE, hold, outbox row
    alt pieces left cover the order and the shopper is within the limit
        T->>O: pieces.held (Requested → Held, with an expiry if the order asked to hold)
        T->>E: pieces.held (the receipt waits while the expiry is set)
        opt pending hold
            C->>O: POST /api/orders/{id}/confirm
            O->>T: pieces.confirm (conditional UPDATE: pins the hold unless the sweeper got there first)
            T->>E: pieces.held (no expiry: now the receipt is issued)
        end
        alt reference is clean
            Note over E: receipt, outbox row
            E->>O: receipt.issued (Held → Sold)
        else reference contains POISON
            Note over E: committed Rejected, then the message is dead-lettered
            E->>T: pieces.release (compensating command)
            E->>O: receipt.rejected (Held → Rejected)
            T->>O: pieces.released
        end
    else deal short, or the shopper is over the limit
        Note over T: the attempt is rolled back, so a limit increment never outlives a failed reserve
        T->>O: pieces.short (Requested → Rejected, no hold, reason Short or Cap)
    end
    Note over T: the sweeper, every instance, every second: unconfirmed holds past their expiry
    T->>O: pieces.expired (Held → Expired), pieces and allowance back
    C->>O: POST /api/orders/{id}/refund
    O->>T: pieces.release (Held or Sold → Refunded)
    T->>O: pieces.released
```

| Module | Owns | Consumes | Emits |
| --- | --- | --- | --- |
| **Identity** (`libs/identity`) | `identity` schema: users, roles, refresh tokens. | | |
| **Orders** (`libs/orders`) | `Orders`: status, pieces, total, shopper reference. Validation, the poison rule, the total. | `pieces.held`, `pieces.short`, `pieces.expired`, `receipt.issued`, `receipt.rejected`, `pieces.released` | `pieces.reserve`, `pieces.reserve`, `pieces.confirm`, `pieces.release` (refund) |
| **Deals** (`libs/deals`) | `Deals.PiecesLeft` (sole writer after the deal opens), `DealShoppers` (what each shopper holds) and `PieceHolds` (with the expiry of a pending hold). | `pieces.reserve`, `pieces.release`, `pieces.confirm` (prefetch 1) | `pieces.held`, `pieces.short`, `pieces.released` |
| **Receipts** (`libs/receipts`) | `Receipts`. | `pieces.held` | `receipt.issued`, `receipt.rejected`, `pieces.release` (poison) |
| **Shared** (`libs/shared`) | The relay, the consumer loop with retry and dead-letter, the Kafka log, auth primitives, middleware. Each module owns its own `OutboxMessages` and `InboxMessages` tables. | | |

RabbitMQ topology is declared in code (`libs/shared/Messaging/Topology.cs`): exchange `flash.work`, one queue per module (`deals.commands`, `receipts.work`, `orders.events`), a `.retry` queue per module (2 s TTL, then back to the main queue, 3 attempts), and a `.dlq` per module behind `flash.dlx`. A transient failure retries; a poison message goes straight to the dead-letter queue after its rejection is committed. Kafka has one topic, `flash.events`, one partition, replication factor 1, keyed by order id.

## Architecture

One deployable (`apps/api`), several libraries. Each business module is its own class library with its own `DbContext`, its own Postgres **schema** (`identity`, `orders`, `deals`, `receipts`), its own migrations and its own outbox and inbox tables. No module references another; the only shared code is `Flash.Shared`, and the one cross-module read (a deal's existence and pricing inputs) goes through an interface, `IDealCatalog`, that Deals implements and Orders consumes.

```mermaid
flowchart TB
    api["apps/api: composition root, middleware, endpoints"]
    api --> identity & orders & pieces & receipts
    identity["libs/identity: users, roles, JWT, refresh tokens"]
    orders["libs/orders: schema orders"]
    pieces["libs/deals: schema deals"]
    receipts["libs/receipts: schema receipts"]
    identity & orders & pieces & receipts --> shared["libs/shared: messaging, outbox/inbox, auth primitives, middleware"]
    orders -. "IDealCatalog" .-> pieces
```

Modules still talk only through messages: RabbitMQ carries the commands, Kafka keeps the event log, and the outbox and inbox make delivery at-least-once and consumption idempotent.

## Authentication and authorization

| Piece | How it works |
| --- | --- |
| Accounts | ASP.NET Core Identity. Passwords need 10+ characters with upper case, lower case and a digit, and 4 distinct characters. Five wrong passwords (or codes) lock the account for 15 minutes (`429`). A wrong password and an unknown email give the same `401`. |
| Email | Registration mails a confirmation link; with `Auth:RequireConfirmedEmail` on (the default) sign-in is refused (`403`) until it is opened. "Forgot password" mails a one-time reset link and answers `202` for every address, known or not. Messages go through `IAuthMailer`: SMTP when `Mail:Host` is set, the log otherwise. The demo stack also keeps them in a dev mailbox at `/api/dev/mailbox` (off by default). Link tokens are protected with data-protection keys stored in Postgres, so every instance and every restart can read them. |
| Two-factor | TOTP (any authenticator app): `POST /api/auth/mfa/setup` returns the key and an `otpauth://` URI, `enable` proves it with a code and returns eight one-time recovery codes, `disable` needs the password and a code. Once on, sign-in needs a code (`401 A verification code is required.`). |
| Access token | JWT, 15 minutes, held **in memory** by the Angular app and sent as `Authorization: Bearer`. HS256 with a key of at least 32 bytes, or **RS256** when `Jwt:RsaPrivateKeyPem` is set, in which case the public key is served at `/.well-known/jwks.json` so other services can verify tokens without a secret. The API refuses to start without an usable key. |
| Revocation | Each token carries the user's session version. A password change, a reset, turning two-factor off and **"sign out everywhere"** bump it (one atomic `UPDATE`), and every token sold earlier stops working on every instance within `Jwt:RevocationCacheSeconds` (default 5). |
| Refresh token | 256 random bits in an `HttpOnly`, `SameSite=Strict`, `Path=/api/auth` cookie (`Secure` over HTTPS). Only its SHA-256 is stored. Every refresh **rotates** it with a conditional `UPDATE`, so two concurrent refreshes with the same token cannot both win. Presenting a token that was already used revokes the whole family. Refresh and logout also need an `X-Requested-With: flash-web` header, so another site cannot trigger them. |
| Roles | `Customer` (default at registration), `Staff`, `Admin`. An administrator is seeded from configuration on first start. |
| Rules | Anonymous: `/api/health`, `/api/auth/*`. Any signed-in user: read deals, book, read and refund **their own** orders and receipts. `Staff` and `Admin`: open deals, read everything, read the event log. Another user's order or receipt is `404`, not `403`, so ids do not leak. The owner of an order is the caller; a body field cannot set it. |
| Messaging | The outbox is drained by any number of relays at once (`FOR UPDATE SKIP LOCKED`, so no row is published twice at the same time) and trimmed on a schedule. Staff can look inside the dead-letter queues and replay them from the API. |
| Hardening | A rate limit on `/api/auth/*` per client (`RateLimit:AuthPerMinute`), **counted in Postgres** so every API instance shares one count, security headers on every response (`nosniff`, `DENY`, strict CSP, `no-referrer`), a correlation id echoed in `X-Correlation-Id` and in every problem response, JSON logs (`Logging:Json`), forwarded headers only when `ForwardedHeaders:Enabled`, a fallback policy that makes every endpoint authenticated unless it opts out, and a readiness endpoint that checks every module schema. |

## Run it

Needs Node 22 (npm 10), .NET SDK 10.0.401 (`global.json`), Docker (arm64 is fine) and python3 for the smoke script.

```bash
npm ci
dotnet test tests/Flash.UnitTests   # 125 tests, no Docker
docker compose up -d --wait db        # the integration tests use Postgres on 5484 (database flash_test)
docker compose exec db createdb -U flash flash_test   # once
dotnet test tests/Flash.IntegrationTests
docker compose up --build --wait      # db, kafka, rabbitmq, api, web
./scripts/smoke.sh                    # last line: flash smoke passed
npx playwright install chromium       # once
npx playwright test                   # the browser specs against http://127.0.0.1:8104
docker compose down                   # keeps the Postgres volume; `down -v` deals it
```

| Service | Host port |
| --- | --- |
| Web (nginx, proxies `/api/`) | 8104 |
| API (`/scalar/v1`, `/api/health`) | 5224 |
| Postgres 17 | 5484 |
| Kafka 3.8.1 (KRaft, one broker) | 9134 |
| RabbitMQ 3.13 / management UI (`platform` / `platform`) | 5714 / 15714 |

Inside the Compose network the API and nginx listen on 8080 and Kafka's internal listener is 9092.

## Try it with curl

The web app is at <http://127.0.0.1:8104> (`admin@flash.local` / `FlashAdmin2026` in the demo stack, or register a customer). From the shell:

```bash
API=http://127.0.0.1:5224
login() { curl -s -X POST $API/api/auth/login -H 'content-type: application/json' -d "{\"email\":\"$1\",\"password\":\"$2\"}" | python3 -c 'import sys,json; print(json.load(sys.stdin)["accessToken"])'; }
ADMIN="Authorization: Bearer $(login admin@flash.local FlashAdmin2026)"

curl -s -X POST $API/api/auth/register -H 'content-type: application/json' \
  -d '{"email":"ana@example.com","password":"Passw0rd-Demo1"}'      # 201; its accessToken goes in $ANA below
ANA="Authorization: Bearer <accessToken>"

curl -s -H "$ADMIN" -X POST $API/api/deals \
  -H 'content-type: application/json' \
  -d '{"code":"GENESIS-01","name":"Night One","aisle":"Home","brand":"Acme Goods","piecesTotal":8,"maxPerShopper":4,"priceCents":8000}'

curl -s -H "$ANA" -X POST $API/api/orders \
  -H 'content-type: application/json' \
  -d '{"dealCode":"GENESIS-01","reference":"SHP-1001","pieces":4}'
# 202, status Requested, totalCents 35840. Use the returned id. A fifth piece for the same account would be refused (limit 4):

curl -s -H "$ANA" $API/api/orders/<id>               # Sold after a few seconds, with a receipt number
curl -s -H "$ANA" $API/api/receipts/<id>
curl -s -H "$ANA" $API/api/deals/GENESIS-01        # piecesLeft 4
curl -s -H "$ADMIN" $API/api/orders/<id>/events        # the Kafka log for this order

curl -s -H "$ANA" -X POST $API/api/orders/<id>/refund \
  -H 'content-type: application/json'                       # pieces go back on sale; the receipt stays
```

An order is validated at the door: shopper reference required (at most 40 characters), 1 to 6 pieces. A deal's `maxPerShopper` (default 2, at most the pieces on sale) caps what one account can hold in it; an order that would pass it is rejected with the reason `Cap` and writes no hold. The poison match is a case-sensitive substring: `SHP-POISON-7` and any longer token containing `POISON` match.

## Develop with Nx

The repository is an Nx workspace (`nx.json`, plugin `@nx/dotnet`). `npx nx show projects` lists `api`, `web`, the four libraries and both test projects.

```bash
docker compose up -d --wait db kafka rabbitmq   # the brokers and Postgres, not the app
npx nx serve api                                # http://localhost:5224 (Development: seeded admin, public docs)
npx nx serve web                                # http://localhost:4304, proxies /api to the API
WEB_URL=http://localhost:4304 npx playwright test   # the same browser specs, against the dev servers
npx nx run-many -t typecheck lint -p web        # tsc --noEmit and ESLint (angular-eslint)
npx nx build web && npx nx build api            # what CI builds
```

## What is tested, and where it runs

- **Unit tests** (`tests/Flash.UnitTests`, 125 tests, no Docker, about 3 seconds): the order rules and fee, the poison rule including integer overflow, token creation and validation, password and lockout behaviour of the auth service on an in-memory store, owner-or-staff access, the retry policy and topology, message serialization, the outbox `DbContext` conventions, and the HTTP middleware (problem details are `application/problem+json`, security headers, correlation ids).
- **Integration tests** (`tests/Flash.IntegrationTests`, 79 tests, real Postgres): the **hold expiry** (a pending hold keeps its pieces until its time runs out and then returns them with the allowance of the shopper; a confirm before the deadline pins it and the sweeper leaves it alone; a confirm after the deadline changes nothing; a confirm and a sweep racing at the deadline, eight rounds, never both win and never lose a piece; four sweepers over twenty expired holds expire each exactly once; a plain reservation is never swept; the receipt waits for the confirmation; an order expires only from `Held`), the pieces race (two tasks, two `DbContext`s, one piece: one `Held`, one `Short`, no hold for the loser), the **limit race** (six concurrent orders by one shopper against a limit of two: exactly two held, four refused, the shopper counter at two), a stock shortage after the limit passed (the limit rolls back), a release giving the allowance back, and the check constraints behind them; the saga handlers against the real schema (duplicate delivery claimed once, a release returns the pieces once, the poison rejection committed before the dead-letter, refund then late final mark); and the **whole HTTP pipeline** through `WebApplicationFactory`: register, login, wrong password, lockout, expired and tampered tokens, refresh rotation, replay revoking the family, two concurrent refreshes (exactly one wins), logout, a missing `X-Requested-With` header, the auth rate limit, and 401/403/404 for every route and ownership case. The race test was run against a read-then-write version of the hold, where it failed, and against the conditional update, where it passes.
- **`scripts/smoke.sh`** drives the composed stack: authentication and ownership first (401, 403, 404), then validation, the happy path (fee, receipt, Kafka log), the last piece with three concurrent shoppers, the per-shopper limit (the third piece is refused as `Cap`, stock untouched), holds (a pending hold has an expiry and no receipt, only its owner confirms, a confirmed hold sells, an unconfirmed one expires and gives its pieces back, confirming an expired or sold order is a 409), the poison reference (rejected, `receipts.work.dlq` gains one message, pieces restored), and a refund that lands between the receipt and the final mark. To make that last window wide enough to hit on purpose, Compose sets `Orders__FinalMarkDelayMs=1500`; it is a latency knob and defaults to 0.
- **Playwright** (`e2e/specs`, 17 tests, Chromium): `auth.spec.ts` (anonymous sees only the form, wrong password, password policy, a customer books and sees only their own orders with no staff controls, sign-out, the session surviving a reload through the cookie while `document.cookie` stays empty); `buy-pieces.spec.ts` (open a deal, book, receipt, reload, refund); `ui-flows.spec.ts` (refusal messages, the poison rejection, the last piece and the per-shopper limit, a refund giving the allowance back, a hold confirmed before it runs out, a hold expiring on its own and the pieces coming back, the Kafka event log). They run against the composed web port and against the Nx dev servers.
- **CI** (`.github/workflows/ci.yml`) has two jobs. `verify`: `npm ci`, Prettier check, `dotnet format whitespace --verify-no-changes`, unit tests, integration tests (Postgres 17 service) with coverage uploaded, a NuGet vulnerability check, `npm audit`, `nx typecheck` and `nx lint` for web (zero warnings allowed) and `nx build`. `e2e`: builds the Compose stack, runs the smoke and every Playwright spec, and uploads traces and prints logs on failure. Dependabot watches NuGet, npm, Actions and Docker.

## Cloud mapping

This repository runs on Compose. Nothing in it calls a cloud account.

| Here | On AWS |
| --- | --- |
| Postgres 17 | RDS for PostgreSQL |
| Kafka 3.8 | MSK |
| RabbitMQ 3.13 | Amazon MQ, or SQS with a dead-letter queue |
| The API and the web app | ECS or App Service |
| JWT signing key and seeded admin | Secrets Manager, injected as environment variables |

## API

JSON over HTTP. Everything except health and `/api/auth/register|login|refresh|logout` needs a bearer token. Interactive docs are at `http://127.0.0.1:5224/scalar/v1` (OpenAPI at `/openapi/v1.json`) when `Docs:Public` is true, which Compose and Development set; otherwise they need a staff token.

| Method and path | What it does | Responses |
| --- | --- | --- |
| `GET /api/health`, `GET /api/health/ready` | Liveness; readiness (every module schema reachable). | `200`, `503` |
| `POST /api/auth/register` | Creates a customer and mails a confirmation link. Signs them in unless confirmation is required. | `201` signed in, `202` check your inbox, `400` policy, `409` email taken, `429` |
| `POST /api/auth/login` | Signs in (with `code` when two-factor is on); sets the refresh cookie. | `200`, `401`, `403` unconfirmed, `429` locked or rate limited |
| `POST /api/auth/refresh`, `POST /api/auth/logout` | Rotate or revoke the refresh cookie. Need `X-Requested-With: flash-web`. | `200`/`204`, `400` header missing, `401` |
| `POST /api/auth/confirm-email`, `resend-confirmation` | Confirm an address with the emailed token; ask for a new link (always `202`). | `204`/`202`, `400` |
| `POST /api/auth/forgot-password`, `reset-password` | Mail a reset link (always `202`); set a new password with it (ends every session). | `202`/`204`, `400` |
| `POST /api/auth/change-password`, `logout-everywhere` | Signed in: change the password (ends the other sessions and returns a fresh one); end every session on every device. | `200`/`204`, `400`, `401` |
| `GET /api/auth/mfa`, `POST /api/auth/mfa/setup`, `enable`, `disable` | Two-factor status and lifecycle (see above). | `200`/`204`, `400`, `409` |
| `GET /.well-known/jwks.json` | The public key set when RS256 is on. | `200`, `404` |
| `GET /api/auth/me` | The caller. | `200`, `401` |
| `POST /api/deals` | Opens a deal with its pieces on sale, a price in cents and an optional `maxPerShopper` (default 2). Staff. | `201`, `400` invalid, `403`, `409` code exists |
| `GET /api/deals`, `GET /api/deals/{code}` | Deals with the live pieces left. | `200`, `404` |
| `POST /api/orders` | Accepts an order (1 to 6 pieces) for the caller and starts the saga. `"hold": true` keeps the pieces without paying until `/confirm` or the expiry. | `202` Requested, `400` invalid, `404` unknown deal, `409` duplicate reference (a deal short of pieces or over the limit is rejected later, by the saga) |
| `GET /api/orders?dealCode=&page=&pageSize=` | The caller's orders, newest first, a page at a time (`pageSize` at most 100, default 100). Staff: everyone's. The total is in `X-Total-Count`. | `200` |
| `GET /api/orders/{id}` | One order and its status. Owner or staff. | `200`, `404` |
| `POST /api/orders/{id}/confirm` | Pays for a held order whose hold has not run out (it pins the hold; the receipt follows). Owner or staff. | `202`, `404`, `409` nothing to confirm or hold expired |
| `POST /api/orders/{id}/refund` | Moves a held (pending or not) or sold order to `Refunded` and puts the pieces back. The receipt is not touched. Owner or staff. | `202`, `404`, `409` wrong status |
| `GET /api/orders/{id}/events` | The Kafka event log for one order. Staff. | `200`, `403` |
| `GET /api/admin/dead-letters`, `POST /api/admin/dead-letters/{queue}/replay` | What waits in each dead-letter queue, with the first messages; publish them again. Staff. A message that still fails for the same reason is dead-lettered again. | `200`, `403`, `503` brokers off |
| `GET /api/receipts/{orderId}` | The receipt row. Owner or staff. | `200`, `404` |

Errors come back as `application/problem+json` with a `title` and the `correlationId`.

## Configuration

Compose sets these on the API container. Defaults in `apps/api/appsettings.json` point at the published host ports, so `nx serve api` works against `docker compose up -d --wait db kafka rabbitmq`.

| Setting | Compose value | Meaning |
| --- | --- | --- |
| `ConnectionStrings__Flash` | `Host=db;Port=5432;Database=flash;Username=flash;Password=flash` | The one Postgres database. Migrations run on startup. |
| `RabbitMq__Host` / `__Port` / `__User` / `__Password` | `rabbitmq` / `5672` / `platform` / `platform` | Work queue. Virtual host `/`. |
| `Kafka__BootstrapServers` | `kafka:9092` | Event log. |
| `Jwt__Key` | `${JWT_KEY}` or a demo key | HS256 signing key, at least 32 bytes. Required; startup fails without it. |
| `Jwt__AccessMinutes` / `__RefreshDays` / `__SecureCookies` | `15` / `14` / `false` | Token lifetimes; set `SecureCookies` when TLS ends in front of the API. |
| `Identity__SeedAdmin__Email` / `__Password` | `${ADMIN_EMAIL}` / `${ADMIN_PASSWORD}` | Administrator created on first start. |
| `Auth__RequireConfirmedEmail` / `Auth__PublicUrl` | `false` (demo) / `http://127.0.0.1:8104` | Sign-in waits for email confirmation (code default `true`); the address links in emails point to. |
| `Mail__Host` / `__Port` / `__User` / `__Password` / `__From` / `__StartTls` | unset | SMTP for confirmation and reset emails. Unset means the log only. |
| `Mail__DevMailbox` | `true` (demo) | Serve the last messages at `/api/dev/mailbox`. Never on in production. |
| `Jwt__RsaPrivateKeyPem` / `Jwt__KeyId` | unset | Sign with RS256 and publish the public key at `/.well-known/jwks.json`. |
| `Jwt__RevocationCacheSeconds` | `2` (code default `5`) | How long an instance trusts a cached session version before re-reading it. |
| `RateLimit__AuthPerMinute` | `120` (code default `20`) | Requests a minute per client to `/api/auth/*`. |
| `ForwardedHeaders__Enabled` | `true` | Trust `X-Forwarded-*` from the reverse proxy (nginx here). Leave off when the API is reached directly. |
| `Docs__Public` | `true` | Serve Scalar and OpenAPI without a token. |
| `Logging__Json` | `true` | Structured JSON console logs. |
| `Messaging__OutboxRetentionDays` / `Messaging__InboxRetentionDays` | `7` / `30` | Sent outbox rows and old inbox claims are deleted after this long. The inbox is kept longer because it must outlive any redelivery. |
| `Deals__HoldSeconds` / `Deals__SweepIntervalMs` | `12` / `500` (code defaults `600` / `1000`) | How long a pending hold keeps its pieces, and how often each instance looks for expired ones. |
| `Orders__FinalMarkDelayMs` | `1500` | Milliseconds the final mark waits. A latency knob so the smoke can land a refund inside the window. `0` outside Compose. |
| `FLASH_TEST_DB` (tests only) | | Postgres connection for `dotnet test`. Defaults to `localhost:5484`, database `flash_test`. CI points it at its service container. |

## Look inside and fix things

```bash
docker compose ps                      # five services, all healthy
docker compose logs -f api             # relay, consumers, poison and retry lines
docker compose exec db psql -U flash -d flash -c 'select "RoutingKey","SentAt" from "OutboxMessages" order by "Id" desc limit 10'
open http://127.0.0.1:15714        # RabbitMQ management, platform / platform
```

- **Dead-lettered messages** sit in `receipts.work.dlq`. Staff can list and replay them from `/api/admin/dead-letters`, or inspect them in the management UI. Retries wait in `<queue>.retry` for 2 seconds.
- **A port is already in use**: `lsof -nP -i :5224` shows who holds it. Compose publishes only on `127.0.0.1`.
- **`dotnet test` cannot connect**: start Postgres first with `docker compose up -d --wait db`.
- **The smoke cannot reach the API**: run `docker compose up --build --wait` first; override the target with `API=... RABBIT=... ./scripts/smoke.sh`.
- **Start clean**: `docker compose down -v` deals Postgres, Kafka and RabbitMQ data. Plain `down` keeps them, and the smoke is safe on kept data because every code carries a run id.
- **The Kafka log looks empty right after an order**: the relay publishes within a second or so; reload the event log.

## Known limits

- The sweeper expires holds within about a second of their deadline (`Deals:SweepIntervalMs`), not exactly on it; a confirm in that second still wins, which is deliberate. The shopper sees the new status on the next refresh, nobody is notified.
- The per-shopper limit is per account. Nothing stops one person from registering a second account; tying it to an identity check is a product decision this repository does not make.
- Sign-in is email and password plus optional TOTP. There are no external identity providers (Google, Microsoft) and no WebAuthn passkeys.
- Revocation is eventual by design: an instance may honour a revoked access token for up to `Jwt:RevocationCacheSeconds` seconds. Set it to `0` to check the database on every request.
- Delivery is at-least-once, not exactly-once. The inbox makes repeats harmless for the saga; the Kafka log can hold a duplicate event after a relay crash.
- Compose runs one of each broker (one Kafka partition, replication factor 1) and one API instance. The code itself is built for several API instances: the relay claims outbox rows with `FOR UPDATE SKIP LOCKED`, and the rate limit and revocation live in Postgres. A production Kafka and RabbitMQ would be clustered; that is configuration, not code.
- `GET .../events` reads the Kafka topic for one key and puts up after ten seconds, so it scans the topic. Fine here, not something to expose at scale.
- Replaying a dead letter only helps when the cause was fixed: the poison reference fails again by design.
- The browser specs drive Chromium at a desktop size only, and the `e2e` CI job needs Docker and takes a few minutes.

## Layout

```
apps/api       thin host: composition root, middleware, endpoints
apps/web       Angular 21, styled with Tailwind 4 utilities only (sign-in, role-aware workspace)
libs/shared    messaging (outbox, inbox, relay, consumers), auth primitives, middleware
libs/identity  users, roles, JWT, refresh tokens          (schema identity)
libs/orders   order rules and the saga's first step      (schema orders)
libs/deals    the only writer of the pieces left and the limit (schema deals)
libs/receipts receipts                              (schema receipts)
tests/         Flash.UnitTests, Flash.IntegrationTests
e2e/           Playwright
scripts/       smoke.sh
compose.yaml   Postgres, Kafka, RabbitMQ, API, web
```

Nx 23.2.1 orchestrates `nx build web` and `nx build api`; Each module's EF Core migrations are committed under its library and the API applies them on startup. Packages are pinned centrally in `Directory.Packages.props`, and warnings are errors (`Directory.Build.props`).

## Other systems in this .NET track

- [dotnet-freight](https://github.com/joshbarros/dotnet-freight): container space on ocean sailings.
- [dotnet-clinic](https://github.com/joshbarros/dotnet-clinic): appointment slots in a provider session.
- [dotnet-ledger](https://github.com/joshbarros/dotnet-ledger): outgoing payments from an account balance.
- [dotnet-warehouse](https://github.com/joshbarros/dotnet-warehouse): warehouse stock and outbound orders.
- [dotnet-academy](https://github.com/joshbarros/dotnet-academy): course cohort seats and enrollments.
- [dotnet-compute](https://github.com/joshbarros/dotnet-compute): GPU-hour reservations in a pool.
- [dotnet-hotel](https://github.com/joshbarros/dotnet-hotel): hotel room-nights.
- [dotnet-tickets](https://github.com/joshbarros/dotnet-tickets): event ticket tiers and orders.
- [dotnet-carbon](https://github.com/joshbarros/dotnet-carbon): carbon credit batches and purchases.
- [dotnet-charging](https://github.com/joshbarros/dotnet-charging): EV charger slot reservations.
- [dotnet-promo](https://github.com/joshbarros/dotnet-promo): limited-use promo codes and redemptions.
- [dotnet-pledge](https://github.com/joshbarros/dotnet-pledge): matched donations from a sponsor's matching pool.
- [dotnet-bistro](https://github.com/joshbarros/dotnet-bistro): table reservations with a first-come waitlist.
- [dotnet-mint](https://github.com/joshbarros/dotnet-mint): limited-edition drops with a per-collector limit.
- [dotnet-encore](https://github.com/joshbarros/dotnet-encore): gig seats with price steps as the gig sells.
- [dotnet-atelier](https://github.com/joshbarros/dotnet-atelier): workshop seats with an all-or-nothing laptop kit bundle.
- [dotnet-sole](https://github.com/joshbarros/dotnet-sole): sneaker raffles with a draw anyone can verify.
