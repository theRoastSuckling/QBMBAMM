using Microsoft.AspNetCore.Mvc;
using QBModsBrowser.Server.Services;

namespace QBModsBrowser.Server.Controllers;

[ApiController]
[Route("api/app-update")]
// Surfaces GitHub release status and triggers the self-updater pipeline for the Installs panel UI.
public class AppUpdateController : ControllerBase
{
    readonly AppUpdateService _svc;

    public AppUpdateController(AppUpdateService svc)
    {
        _svc = svc;
    }

    // Returns current/remote version info; result is cached for 60s on the service side.
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        var status = await _svc.GetStatusAsync();
        return Ok(status);
    }

    // Kicks off download + extract + detached updater bat. On success the server will exit shortly after responding.
    [HttpPost("install")]
    public async Task<IActionResult> Install()
    {
        var started = await _svc.DownloadAndInstallAsync();
        if (!started) return Conflict(new { error = "No update available or install already in progress" });
        return Accepted();
    }
}
