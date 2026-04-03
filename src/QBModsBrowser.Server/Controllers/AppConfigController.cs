using Microsoft.AspNetCore.Mvc;
using QBModsBrowser.Server.Models;

namespace QBModsBrowser.Server.Controllers;

[ApiController]
[Route("api/app-config")]
// Exposes read-only application configuration (loaded from app-config.json at the project root) to the frontend.
public class AppConfigController : ControllerBase
{
    private readonly AppConfig _config;

    public AppConfigController(AppConfig config)
    {
        _config = config;
    }

    // Returns the full app config so the frontend can consume values like Discord IDs without hardcoding them.
    [HttpGet]
    public IActionResult Get() => Ok(_config);
}
