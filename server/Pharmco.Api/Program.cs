// ============================================================================
// PHARMCO API — entry point (ASP.NET Core 8 "minimal API" style).
//
// STATUS: Phase-0 scaffold. This file is intentionally dependency-free so that
// `dotnet run` compiles out of the box on .NET 8. It wires the health endpoint
// and leaves clearly-marked wiring points for the Phase-1 modules, which land
// week-by-week per docs/architecture.md:
//
//   Week 1  tenant provisioning  -> /v1/admin/tenants        (creator API)
//   Week 2  auth                 -> /v1/auth/token|refresh
//   Week 3  catalog/inventory    -> /v1/products|stock
//   Week 5  sync inbound         -> /v1/sync
//   Week 6  M-Pesa               -> /v1/mpesa/stkpush + /v1/mpesa/callback
//
// Scaffold was authored in a sandbox without the .NET SDK — compile once on a
// machine with .NET 8 (see server/Pharmco.Api/README.md for the command).
// ============================================================================

using Pharmco.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();

// --- published static files (if ever needed server-side — usually served by Caddy)
app.UseDefaultFiles();
app.UseStaticFiles();

// --- probe used by docker healthcheck + Uptime Kuma -------------------------
app.MapGet("/health", Health.Handle);

// --- Phase-1 wiring points (implementations land with their week) -----------
//   var admin = app.MapGroup("/v1/admin");
//   admin.AddAdminEndpoints();        // tenant provisioning + license (Weeks 1-2)
//
//   var v1 = app.MapGroup("/v1");
//   v1.AddAuthEndpoints();            // token / refresh                    (Week 2)
//   v1.AddProductEndpoints();         // catalog + stock                    (Week 3)
//   v1.AddSalesEndpoints();           // sale finalize (hub -> cloud)       (Week 4)
//   v1.AddSyncEndpoints();            // outbox drain + inbound push        (Week 5)
//   v1.AddMpesaEndpoints();           // STK push + Daraja callback         (Week 6)

app.Run();

public partial class Program { }