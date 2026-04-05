using Microsoft.AspNetCore.Mvc;
using System.Reflection;

namespace QBModsBrowser.Server.Controllers;

[ApiController]
[Route("api/app-info")]
// Exposes runtime application metadata to the frontend (version read from assembly attributes).
public class AppInfoController : ControllerBase
{
    // Returns the app version string derived from AssemblyInformationalVersion (set by <Version> in the .csproj).
    // Strips the "+<git-hash>" build-metadata suffix that MSBuild appends automatically.
    [HttpGet]
    public IActionResult Get()
    {
        var raw = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
        var clean = raw.Contains('+') ? raw[..raw.IndexOf('+')] : raw;
        return Ok(new { version = $"v{clean}" });
    }
}
