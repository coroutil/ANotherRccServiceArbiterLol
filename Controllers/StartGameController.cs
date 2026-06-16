using Microsoft.AspNetCore.Mvc;
using System.Data;
using System.Diagnostics;
using static Arbiter.GameMonitorService;

namespace Arbiter.Controllers;

[ApiController]
[Route("[controller]")]
public class StartGameController : ControllerBase
{
    [HttpPost]
    public IActionResult Post([FromBody] StartGameRequest request)
    {
        /* validation check start */
        if (!Request.Headers.TryGetValue("Authorization", out var authHeader))
            return Error.Create(401, "Unauthorized");

        var AccessKey = Configuration.GetStringFlag("DFStringAccessKey");
        var token = authHeader.ToString();

        if (!token.StartsWith("Bearer ") || token.Substring("Bearer ".Length).Trim() != AccessKey) {
            return Error.Create(401, "Unauthorized");
        }
        /* validation check end */

        var args = Helper.ParseArguments(request.Arguments);
        var script = ScriptResolver.GetScript(request.Type);
        var jobId = Guid.NewGuid().ToString();

        if (request == null)
            return Error.Create(400, "BadRequest");

        if (string.IsNullOrWhiteSpace(request.Type))
            return Error.Create(400, "BadRequest");

        if (request.Id <= 0)
            return Error.Create(400, "BadRequest");

        // if rccserviec only speaks json, then oh dear we need to tamper with the script
        // to do this, we make our own programming language!
        if (Configuration.GetFlag("FFlagRCCServiceOnlySpeaksJSON"))
        {
            // Here, we will use the arguments.
            // so, for example, we get example arguments: "67,string,bool"
            // {} means "take the next argument and insert it here". so we do that for all arguments, and then we have a nice json object to send to rccservice
            // so a line can be like from this: "spawnPlayer({}, {}, true)"
            // to this: "spawnPlayer(67, string, true)"

            script = Helper.ProcessArguments(script, args);
        }

        try
        {

            if (request.Type.Equals("gameserver", StringComparison.OrdinalIgnoreCase)) {
                var rcc = RCCService.Start(Helper.GetAvailablePort(Configuration.GetIntFlag("DFIntRCCServiceMinPort"), Configuration.GetIntFlag("DFIntRCCServiceMaxPort"), "TCP")); // THIS IS BULLSHIT

                if (rcc == null)
                    return Error.Create(503, "ServiceUnavailable");

                var raknetPort = Helper.GetAvailablePort(Configuration.GetIntFlag("DFIntGameServerMinPort"), Configuration.GetIntFlag("DFIntGameServerMaxPort"), "UDP");

                int publicPort = raknetPort;
                ReverseProxy? proxy = null;

                if (Configuration.GetFlag("FFlagUseReverseProxy"))
                {
                    publicPort = Helper.GetAvailablePort(Configuration.GetIntFlag("DFIntReverseProxyMinPort"), Configuration.GetIntFlag("DFIntReverseProxyMaxPort"), "UDP");
                    proxy = new ReverseProxy(publicPort, raknetPort);
                    proxy.Start();
                }

                args.Insert(0, LuaValue.FromNumber(raknetPort));

                _ = Task.Run(() =>
                {
                    SOAP.Send(
                        rcc.Port,
                        "OpenJobEx",
                        script,
                        "OpenJobEx",
                        out var rccvalue,
                        jobId,
                        arguments: args,
                        expirationInSeconds: 30,
                        cores: Math.Max(1, Health.GetPhysicalCoreCount() / Process.GetProcessesByName(Configuration.GetStringFlag("FStringRCCServiceName")).Length),
                        category: 1
                    );
                });

                GameMonitorService.Insert(new GMSJob
                {
                    JobId = jobId,
                    Port = publicPort,
                    SOAP = rcc.Port,
                    PlaceId = request.Id,
                    Pid = rcc.Process.Id,
                });

                return Ok(new
                {
                    jobId,
                    port = publicPort,
                    pid = rcc.Process.Id
                });
            }
            else
            {
                var rcc = RCCServicePool.Acquire();

                if (rcc == null)
                    return Error.Create(503, "ServiceUnavailable");

                var response = SOAP.Send(
                    rcc.Port,
                    "BatchJobEx",
                    script,
                    "BatchJobEx",
                    out var rccvalue,
                    jobId,
                    arguments: args,
                    expirationInSeconds: 60, // one minute for a render and thats good enough
                    cores: Math.Min(2, Health.GetPhysicalCoreCount()),
                    category: 2
                );

                if (string.IsNullOrWhiteSpace(rccvalue))
                    return Error.Create(500, "InternalServerError");

                byte[] bytes;

                try
                {
                    bytes = Convert.FromBase64String(rccvalue);
                }
                catch (FormatException)
                {
                    return Error.Create(500, "InternalServerError");
                }

                rcc.Process.Kill(true); // we kill the rcc after a render. rcc is designed to do one job at a time, as particles will break
                RCCServicePool.Kill(rcc);

                return File(bytes, "image/png");
            }
        }
        catch (Exception ex)
        {
            return Error.Create(500, ex.Message);
        }
    }
}

public sealed class StartGameRequest
{
    public string Type { get; set; } = string.Empty;
    public long Id { get; set; }

    // 67,string,true
    public string Arguments { get; set; } = string.Empty;
}