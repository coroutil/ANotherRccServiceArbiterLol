using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace Arbiter.Controllers;

[ApiController]
[Route("[controller]")]
public class ExecuteScriptController : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] ExecuteScriptRequest body)
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

            var response = await SOAP.Send(
                port: job.SOAP,
                jobType: "ExecuteScript",
                script: body.script,
                action: "ExecuteScript",
                jobId: body.gameId,
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

public sealed class ExecuteScriptRequest
{
    public string scriptName { get; set; } = string.Empty;
    public string gameId { get; set; } = string.Empty;
    public string script { get; set; } = string.Empty;

    // ["string", 67, true]
    public List<object> arguments { get; set; } = new();
}