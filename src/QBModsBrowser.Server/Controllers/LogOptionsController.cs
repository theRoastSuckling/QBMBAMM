using Microsoft.AspNetCore.Mvc;
using QBModsBrowser.Server.Utilities;

namespace QBModsBrowser.Server.Controllers;

[ApiController]
[Route("api/log-options")]
// Exposes runtime log-filter toggles so the UI can turn verbose endpoint logging on/off without a restart.
public class LogOptionsController : ControllerBase
{
    // Returns the current runtime log options.
    [HttpGet]
    public IActionResult GetLogOptions()
    {
        return Ok(new { showPollingLogs = LogOptions.ShowPollingLogs });
    }

    // Applies new log options; changes take effect immediately for all subsequent log events.
    [HttpPut]
    public IActionResult PutLogOptions([FromBody] LogOptionsDto dto)
    {
        LogOptions.ShowPollingLogs = dto.ShowPollingLogs;
        return Ok(new { showPollingLogs = LogOptions.ShowPollingLogs });
    }
}

// DTO for updating log options.
public record LogOptionsDto(bool ShowPollingLogs);
