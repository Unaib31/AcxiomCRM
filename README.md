# AcxiomCRM

A role-based CRM covering the full customer-sales lifecycle: lead capture → qualification → conversion → opportunity pipeline → follow-ups, with authentication, authorization, validation, audit logging, a secured REST API and reporting. Built to the *AcxiomCRM Project Documentation* specification.

| | |
|---|---|
| **Stack** | ASP.NET Core 8 MVC · ASP.NET Core Identity · Entity Framework Core 8 (SQLite) · Bootstrap 5 · Chart.js · jQuery unobtrusive validation |
| **API** | REST + JWT bearer, OpenAPI/Swagger (Development) |
| **Tests** | 166 automated tests (unit + full-stack integration against a real database) |

## Quick start (development)

Prerequisite: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
cd src/AcxiomCRM.Web
dotnet run            # http://localhost:5080  (Development profile)
```

On first start the database is created and migrated automatically, roles are seeded and — **in Development only** — demo accounts and sample data are added:

| Role | Email | Password |
|---|---|---|
| Admin | `admin@acxiom.local` | `Welcome#2026x` |
| Manager | `manager@acxiom.local` | `Crm#Demo2026` |
| Sales Executive | `arjun@acxiom.local`, `priya@acxiom.local` (both report to the manager) | `Crm#Demo2026` |
| Sales Executive (no manager) | `rahul@acxiom.local` | `Crm#Demo2026` |

These live in `appsettings.Development.json` and are refused outside Development (the app will not start with `Seed:DemoData=true` in any other environment).

Swagger UI: `http://localhost:5080/swagger` (Development only). Health probe: `/health`.

```bash
dotnet test           # from the repository root: 166 tests
```

## Roles and authorization

Authorization is enforced **on the server** for pages and API alike (navigation hiding is only cosmetic).

| Module | Admin | Manager | Sales Executive |
|---|---|---|---|
| Dashboard, Customers, Leads, Opportunities, Follow-ups, Activities | everything | own + direct reports' records | records assigned to them |
| Delete customers / leads / opportunities / follow-ups / activities | ✔ | ✔ (within team) | ✘ (can cancel follow-ups) |
| Assign records to | any active user | self or team | self only |
| Users | full administration | read-only view of own team (configurable) | ✘ |
| Roles & permissions | ✔ | ✘ | ✘ |
| Audit log | full, with old/new values and IP | business events of the team, no security events (configurable) | ✘ |
| Reports | all | team | own scope |
| REST API | same rules as above; `/api/reports/pipeline` is Manager/Admin | | |

Mechanics: every query goes through `IAccessScope` (`Application/Services/AccessScopeService.cs`); records outside the caller's scope return **404** (they are indistinguishable from non-existent ones), role-restricted actions return **403**. A fallback policy requires authentication for everything not explicitly `[AllowAnonymous]`.

## Architecture

```
src/AcxiomCRM.Web
├── Domain/            Entities, enums, business rules (lead workflow, opportunity/follow-up rules, validation patterns)
├── Data/              AppDbContext (+ automatic audit capture), migrations, DbInitializer, demo data
├── Application/
│   ├── Common/        ServiceResult, paging, clock, current user, access-scope contracts
│   ├── Dtos/          Input models (shared by MVC forms and the API) and response DTOs
│   ├── Security/      AuthService, JWT service, password-reuse validator, options
│   └── Services/      Customer, Lead, Opportunity, FollowUp, Activity, User admin, Audit, Dashboard, Report, Search
├── Controllers/       MVC controllers; Controllers/Api = REST controllers
├── ViewModels/ Views/ ViewComponents/   Razor UI
├── Infrastructure/    Security headers, problem-details, client-validation attributes, <crm-field> tag helper
└── wwwroot/           Locally vendored Bootstrap, icons, Chart.js, jQuery (no CDN; strict CSP)
tests/AcxiomCRM.Tests  xUnit unit + integration tests (WebApplicationFactory, isolated SQLite DB per fixture)
```

Layering follows the spec: presentation → application services → domain rules → data access (EF Core) → database, with security, logging, validation and auditing as cross-cutting concerns.

## Security controls

| Control | Where / how |
|---|---|
| Password hashing | ASP.NET Core Identity (PBKDF2, per-user salt). Plain text is never stored or logged. |
| Password policy | Min 8 chars, upper + lower + digit + symbol, 4 unique chars, must not contain the email name, **no reuse of the last 5 passwords** (`PasswordHistory` stores hashes only). All configurable under `Security:*`. |
| Account lockout | 5 failed attempts → 15-minute lockout (configurable). Lockout, unlock, failures and successes are audited. Admin can unlock. |
| Sessions | Auth cookie `HttpOnly`, `SameSite=Strict`, `Secure` outside Development, sliding 30 min. Deactivation, role change or password change invalidates sessions immediately (security stamp checked every request). |
| CSRF | Global `AutoValidateAntiforgeryToken` for every state-changing MVC request; API uses bearer tokens (no ambient credentials). |
| API auth | JWT (HS256, 60 min) with per-request check that the user is still active, unlocked, on the same security stamp and the token is not revoked (`/api/auth/logout` revokes). |
| Rate limiting | Login, register, reset endpoints (10/min per IP by default) → `429`. |
| Injection | EF Core parameterised queries only; `LIKE` wildcards in user input are escaped; sort columns are whitelisted. |
| XSS | Razor encoding everywhere; CSP `default-src 'self'` with **no inline script/style**; CSV exports neutralise formula injection. |
| Headers | CSP, `X-Content-Type-Options`, `X-Frame-Options: DENY`, `Referrer-Policy`, `Permissions-Policy`, `Cache-Control: no-store`, HSTS + HTTPS enforcement in Production. |
| Mass assignment / IDOR | Input DTOs contain no ownership/role fields; assignee is validated against the caller's authority; self-registration can only ever create a Sales Executive. |
| Errors | RFC 7807 problem details for the API, friendly pages for the UI; no stack traces, SQL or internals are exposed. |
| Audit | Append-only (blocked in code **and** by database triggers); captures user, time, IP, module, action, entity/record, old & new values, result. Secrets are excluded by design. |
| Secrets | No secret is in source control. `Jwt:Key`, admin seed credentials and the connection string come from environment variables / user secrets. Production refuses to start without a JWT key. |
| Least privilege | Container runs as non-root, read-only root filesystem, all capabilities dropped. |

## Validation

* **Client side** (jQuery unobtrusive, attributes emitted from the same models): required, e-mail, phone, length, date and numeric rules, plus the business rules for amount / probability / dates with the exact messages from the spec (*“Opportunity Amount must be greater than 0.”*, *“Probability must be between 0 and 100.”*, *“Expected Close Date cannot be in the past.”*, *“Follow-up date cannot be earlier than today.”*, *“Enter a valid email address.”*, *“Enter a valid phone number.”*).
* **Server side** (authoritative): the same DataAnnotations are re-run inside every service, followed by business rules, foreign-key/ownership checks and duplicate detection. Crafted requests cannot bypass them (covered by tests).
* **Phone**: Indian mobile, 10 digits starting 6–9, optional `+91`/`91`/`0` prefix; stored as the 10 digits (`ValidationRules.PhonePattern` – change it in one place for another country). **Dates** use the configured time zone (`Clock:TimeZoneId`, default `Asia/Kolkata`).
* **Business rules**: opportunity amount > 0 for active deals (Won also needs > 0), probability 0–100 (Won = 100, Lost = 0), close date not in the past while active, closed deals are final; follow-up date ≥ today for new/planned follow-ups; lead status workflow (`New → Contacted → Qualified → Converted`, with `Unqualified`/`Lost` branches; `Converted` only through the conversion action); duplicate customers rejected by email, phone, and name + company; a customer with active opportunities cannot be deleted.

## REST API

Obtain a token, then call any endpoint with `Authorization: Bearer <token>`.

```bash
TOKEN=$(curl -s -X POST http://localhost:5080/api/auth/login -H 'Content-Type: application/json' \
  -d '{"userName":"arjun@acxiom.local","password":"Crm#Demo2026"}' | jq -r .accessToken)
curl -s http://localhost:5080/api/customers?search=sharma -H "Authorization: Bearer $TOKEN"
```

| Method | Endpoint | Purpose |
|---|---|---|
| POST | `/api/auth/login` · `/api/auth/logout` | Authenticate (rate limited) · revoke token |
| GET / POST | `/api/customers` | List/search (`search,status,assignedTo,sort,desc,page,pageSize≤100`) · create |
| GET / PUT / DELETE | `/api/customers/{id}` | Get · update · deactivate (Manager/Admin) |
| GET / POST | `/api/leads` | List · create |
| GET / PUT / DELETE | `/api/leads/{id}` | Get · update · delete (Manager/Admin) |
| POST | `/api/leads/{id}/status` · `/api/leads/{id}/convert` | Workflow transition · convert Qualified lead |
| GET / POST | `/api/opportunities` | List · create |
| GET / PUT / DELETE | `/api/opportunities/{id}` | Get · update · delete (Manager/Admin) |
| POST | `/api/opportunities/{id}/stage` | Move stage / mark Won or Lost |
| GET / POST | `/api/followups` (`view=upcoming|overdue|today|all`) | List · create |
| GET / PUT / POST | `/api/followups/{id}` · `/api/followups/{id}/complete` | Get · reschedule · complete |
| GET | `/api/reports/pipeline` | Stage- and owner-wise pipeline (Manager/Admin) |

Status codes: `200/201/204`, `400` (validation, with per-field `errors`), `401`, `403`, `404`, `409` (duplicates, conflicts, workflow violations), `429`. Responses are DTOs only — never password hashes, security stamps or internal fields. Full request/response models are in Swagger.

## Configuration

Any setting can be supplied as an environment variable using `__` as separator (e.g. `Security__SessionMinutes=20`).

| Key | Default | Notes |
|---|---|---|
| `ConnectionStrings:Default` | `Data Source=App_Data/acxiomcrm.db` | SQLite file |
| `Jwt:Key` | – | **Required** outside Development, ≥ 32 chars (`openssl rand -base64 48`) |
| `Seed:AdminEmail` / `Seed:AdminPassword` | – | Creates the first administrator if missing; remove the password after first start |
| `Clock:TimeZoneId` | `Asia/Kolkata` | Defines “today” for date rules |
| `Security:Password:*`, `Security:Lockout:*`, `Security:PasswordHistoryCount` | see `appsettings.json` | Password and lockout policy |
| `Security:SessionMinutes`, `Security:ResetTokenHours`, `Security:AuthRateLimitPerMinute` | 30 / 2 / 10 | |
| `Security:AllowSelfRegistration` | `true` | Set `false` to make user creation admin-only |
| `Security:ManagerCanViewUsers`, `Security:ManagerCanViewAudit` | `true` | Manager limited views |
| `Security:RequireHttps`, `Security:UseForwardedHeaders`, `Security:HttpsPort` | prod: `true` / `false` / – | See deployment |
| `DataProtection:KeyPath` | `App_Data/keys` | Persist and back up (cookie / reset-token keys) |

## Production deployment

1. Terminate TLS in front of the app (or give Kestrel a certificate). `RequireHttps` is on in Production: plain-HTTP requests are redirected, or rejected with `400` if no HTTPS port is known. Cookies are `Secure`, so the app must be reached over HTTPS.
2. Behind a reverse proxy set `Security__UseForwardedHeaders=true` and make sure only the proxy can reach the app (the proxy list is intentionally not hard-coded).
3. Provide secrets via environment variables / your secret manager: `Jwt__Key`, `Seed__AdminEmail`, `Seed__AdminPassword`, and `ConnectionStrings__Default`.
4. Persist `/data` (database + data-protection keys). Back both up; for stronger protection of the key ring configure a certificate/KMS (`ProtectKeysWith…`).
5. Docker: `cp .env.example .env`, fill it in, `docker compose up -d --build`, then put your TLS proxy in front of `127.0.0.1:8080`. *(The Dockerfile/compose files were written to the same configuration the tests exercise but could not be built in the authoring environment because no Docker daemon was available – run one build before relying on them.)*
6. **Other databases:** the model is provider-agnostic. For SQL Server swap `UseSqlite` for `UseSqlServer`, delete `Data/Migrations`, regenerate with `dotnet ef migrations add InitialCreate`, change the filtered-index SQL (`"IsDeleted" = 0` → `[IsDeleted] = 0`), remove the `double` money / `LockoutEnd` converters, and re-create the audit append-only protection as a trigger or permission denial.

## Spec traceability

| Spec section | Implementation |
|---|---|
| 4.1 / 12 Shell, UI/UX | `_Layout`, role-based menu, global search, notifications bell, alerts, confirm dialogs, paging/sorting/filtering on lists, responsive Bootstrap, accessible labels, skip link |
| 4.2 / 6 / 17.4 / 17.9 Auth & security | `AccountController`, `AuthService`, Identity (hashing, policy, lockout), login/register/logout, change password, admin-issued reset link |
| 4.3 / 17.11–12 Dashboard | `DashboardService` + Chart.js (lead status, pipeline, monthly sales), date filters (Today / This week / This month / Custom) |
| 4.4 / 17.5 Customers | `CustomerService`, CRUD, search, duplicate rules, history & related records |
| 4.5 / 17.6 Leads | `LeadService`, workflow, assignment, conversion to customer/opportunity |
| 4.6 Follow-ups | `FollowUpService`, upcoming/overdue/today views, complete/miss/cancel/reschedule, audited |
| 4.7 Users & roles | `UserAdminService`, `UsersController` (create, edit, activate/deactivate, unlock, reset link, role & permission matrix) |
| 4.8 / 17.7 Opportunities | `OpportunityService`, pipeline board, weighted pipeline, final Won/Lost |
| 17.2 Activities | `ActivityService` (call, meeting, email, task), auto-logged when follow-ups complete |
| 4.9 / 17.16 Audit | `AppDbContext` (automatic), `AuditService` (explicit events), `AuditQueryService`, filters + CSV export |
| 4.10 / 10 / 17.14 API | `Controllers/Api`, DTOs, problem details, Swagger |
| 4.11 / 11 Reports | `ReportService` – 8 reports with filters, paging, CSV export, role scoping |
| 5 / 17.8 Validation | `Dtos`, `Domain/Rules`, `Infrastructure/ClientValidation`, `site.js` adapters |
| 7 / 17.10 Authorization | `AccessScopeService`, policies in `Program.cs`, per-service role checks |
| 13 / 14 / 17.19 Acceptance & security checklists | `tests/AcxiomCRM.Tests` (authentication, lockout, authorization/IDOR, validation bypass, audit, reports, headers) |

## Design decisions worth knowing

* **Self-registration is on by default** (the spec's acceptance scenario uses *Register*) but can only create a least-privileged Sales Executive; turn it off with `Security:AllowSelfRegistration=false`.
* **Password reset** is administrator-issued (a single-use, expiring link shown once) because the spec defines no e-mail infrastructure. Plug an e-mail sender into `UsersController.ResetLink` to automate it.
* **Soft delete** for customers, leads and opportunities keeps history and audit references intact; email/phone of a deleted customer can be reused.
* **Money** is stored as `REAL` in SQLite so ordering/aggregation work; amounts are rounded to 2 decimals and aggregated in memory in a few places. Use a decimal column on SQL Server.
