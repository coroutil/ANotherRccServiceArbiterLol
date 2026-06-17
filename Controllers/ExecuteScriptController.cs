using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace Arbiter.Controllers;

public record ExecuteScript(
    string gameId,
    string scriptName,
    string arguments,
    string script
);

[ApiController]
[Route("[controller]")]
public class ExecuteScriptController : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] ExecuteScript body)
    {
        /* validation check start */
        if (!Request.Headers.TryGetValue("Authorization", out var authHeader))
            return Error.Create(401, "Unauthorized");

        var AccessKey = Configuration.GetStringFlag("DFStringAccessKey");
        var token = authHeader.ToString();

        if (!token.StartsWith("Bearer ") || token.Substring("Bearer ".Length).Trim() != AccessKey)
        {
            return Error.Create(401, "Unauthorized");
        }
        /* validation check end */

        if (body == null || string.IsNullOrWhiteSpace(body.gameId) || string.IsNullOrWhiteSpace(body.scriptName) || string.IsNullOrWhiteSpace(body.script))
        {
            return Error.Create(400, "BadRequest");
        }

        var job = GameMonitorService.Get(body.gameId);

        if (job == null)
            return Error.Create(404, "NotFound");

        try
        {
            var args = Helper.ParseArguments(body.arguments);

            var response = SOAP.Send(
                job.SOAP,
                "ExecuteScript",
                body.script,
                "ExecuteScript",
                out var rccvalue,
                body.gameId,
                expirationInSeconds: 30,
                arguments: args
            );

            return Ok(new
            {
                success = true
            });
        }
        catch (Exception ex)
        {
            return Error.Create(500, ex.Message);
        }
    }
}