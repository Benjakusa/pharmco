# Pharmco.Api — server scaffold

ASP.NET Core 8 **minimal API** service. **Status: scaffold + Week-2 auth.** The
Phase-0 scaffold was authored in a sandbox without the .NET SDK, so source has
**not been compiled here**; it uses only base ASP.NET Core APIs and should
build first try with:

```bash
# on a machine with .NET 8 SDK (or via devcontainer)
cd pharmco/server/Pharmco.Api
dotnet run                 # serves GET /health on :8080
dotnet test                # from ../Pharmco.Tests
```

## Layout
| Path            | Role |
|-----------------|------|
| `Program.cs`    | entry + wiring; health; auth/user routes |
| `Endpoints/`    | `Health` · `AuthEndpoints` (login/refresh/logout/session) · `UserEndpoints` (admin CRUD) · `Authz` (role guard) |
| `Services/`     | `JwtService` (hand-rolled HS256) · `RateLimiter` (+ config) |
| `../Pharmco.Core/Auth/` | `AuthRepository` (users/refresh/audit), `PasswordService`, `UserRole`, DTOs, `OfflineGate`/`IdlePolicy` (shared with client) |

## Endpoints (Week 2)
| method | path | auth |
|---|---|---|
| POST | `/api/auth/login` | public — `{ pharmacy_code, username, password }` |
| POST | `/api/auth/refresh` | public — `{ refresh_token }` (rotates) |
| POST | `/api/auth/logout` | bearer |
| GET  | `/api/auth/session` | bearer — cache validation for the desktop client |
| GET/POST | `/api/users`, PATCH/DELETE `/api/users/{id}` | admin only (`[Authorize(Roles = "admin")]`) |

Contract details: `docs/api-contract.md`. Roles: admin/pharmacist/cashier.

## Environment (see deploy/.env.example)
| var | purpose |
|-----|---------|
| `ConnectionStrings__Master` | Postgres master (schema `master`) |
| `Jwt__Secret` | HS256 signing key |
| `Jwt__AccessTtlMinutes` | access token TTL (default 15) |
| `Jwt__RefreshTtlDays` | refresh token TTL (default 30) |
| `RateLimit__MaxAttempts` | failed logins before 429 (default 5) |
| `RateLimit__WindowMinutes` | rate-limit window (default 15) |
| `Jwt__AdminKey` | shared secret for `/v1/admin/*` (`X-Admin-Key`) |
| `Daraja__EncryptionKey` | AES-256-GCM key for per-tenant M-Pesa secrets |
| `License__PrivateKeyPath` | CLI license signing key |