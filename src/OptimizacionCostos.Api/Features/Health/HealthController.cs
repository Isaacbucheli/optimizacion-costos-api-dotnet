using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace OptimizacionCostos.Api.Features.Health;

[ApiController]
[Route("health")]
[AllowAnonymous]
public sealed class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { status = "ok", service = "optimizacion-costos-api-dotnet" });
}
