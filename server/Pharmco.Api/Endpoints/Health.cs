namespace Pharmco.Api.Endpoints;

/// <summary>/health — docker healthcheck + Uptime Kuma probe.</summary>
public static class Health
{
    public static IResult Handle()
        => Results.Ok(new
        {
            status = "ok",
            service = "pharmco-api",
            version = "0.1.0",
        });
}