using Microsoft.AspNetCore.Mvc;
using System.Windows.Forms;

namespace QBModsBrowser.Server.Controllers;

[ApiController]
[Route("api/app")]
// App lifecycle endpoints (shutdown, etc.).
public class AppController : ControllerBase
{
    // Requests shutdown of the running server instance.
    // Used by a newer version of the exe to take over from the currently running one.
    // Application.Exit() unblocks tray.Run() on the STA thread so Program.cs can clean up
    // the tray icon before force-exiting. The short delay lets the HTTP response return first.
    [HttpPost("shutdown")]
    public IActionResult Shutdown()
    {
        Task.Run(async () =>
        {
            await Task.Delay(300);
            Application.Exit();
        });
        return Ok();
    }
}
