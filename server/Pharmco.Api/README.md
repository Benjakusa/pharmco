# Pharmco.Api — server scaffold

ASP.NET Core 8 **minimal API** service. **Status: Phase-0 scaffold** — `Program.cs`
wires `/health` only. The scaffold was authored in a sandbox without the .NET SDK,
so it has **not been compiled here**; it uses only base ASP.NET Core APIs and should
build first try with:

```bash
# on a machine with .NET 8 SDK (or via devcontainer)
cd pharmco/server/Pharmco.Api
dotnet run                 # serves GET /health on :8080
dotnet test
```

## Layout
| Path            | Role |
|-----------------|------|
| `Program.cs`    | entry; health endpoint; Phase-1 wiring points (commented) |
| `Endpoints/`    | endpoint modules land here week-by-week |
| `Models/`       | response/request records (DTOs) |
| `Services/`     | persistence (Dapper + Postgres), JWT, license, Daraja adapter |

## Phase-1 dependency additions (week 1, server/Pharmco.Api.csproj)
- Dapper (raw SQL for speed — we own the schema), postgres driver for C#.
- JWT sign/verify (HS256 access 15 min): a small hand-rolled or library helper.
- bcrypt cost 12 for passwords (server-side).
- AES-256-GCM encrypt/decrypt helpers for `daraja_config` (keys from env).
- Serilog → file for cheap structured logging (monitoring via Uptime Kuma).

## Environment (see deploy/.env.example)
| var | purpose |
|-----|---------|
| `ConnectionStrings__Master` | Postgres master (schema `public`) |
| `Jwt__Secret` | HS256 signing key |
| `Jwt__AdminKey` | shared secret for `/v1/admin/*` (`X-Admin-Key`) |
| `Daraja__EncryptionKey` | AES-256-GCM key for per-tenant M-Pesa secrets |

## Non-goals for the scaffold
No ORM beyond Dapper, no OpenAPI codegen, no micromanaged DI. One Postgres
connection pool, `search_path`-based tenant routing via the JWT `tenant_id`
(contract in `docs/api-contract.md`).