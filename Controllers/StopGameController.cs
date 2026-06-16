using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

namespace Arbiter.Controllers;

public record KillRequest(long pid);

[ApiController]
[Route("[controller]")]
public class StopGameController : ControllerBase
{
    [HttpPost]
    public IActionResult Post([FromBody] KillRequest request)
    {
        /* validation check start */
        if (!Request.Headers.TryGetValue("Authorization", out var authHeader))
            return Error.Create(401, "Unauthorized");

        var accessKey = Configuration.GetStringFlag("DFStringAccessKey");
        var token = authHeader.ToString();

        if (!token.StartsWith("Bearer ") || token.Substring("Bearer ".Length).Trim() != accessKey)
        {
            return Error.Create(401, "Unauthorized");
        }
        /* validation check end */

        if (request == null || request.pid <= 0)
            return Error.Create(400, "BadRequest");

        try
        {
            var job = GameMonitorService.GetByPID((int)request.pid);

            if (job == null)
                return Error.Create(404, "NotFound");

            GameMonitorService.Remove(job.JobId);
            ReverseProxy.Stop(job.Port); // stop reverse proxy as well if it exists

            var process = Process.GetProcessById((int)request.pid);

            process.Kill(true);
            process.WaitForExit();

            return Ok(new
            {
                success = true,
            });
        }
        catch (ArgumentException)
        {
            return Error.Create(404, "NotFound");
        }
        catch (Exception ex)
        {
            return Error.Create(500, ex.Message);
        }
    }
}