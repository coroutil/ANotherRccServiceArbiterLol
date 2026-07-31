using Microsoft.AspNetCore.Mvc;
using System.Data;
using System.Diagnostics;
using System.Text;
using static Arbiter.GameMonitorService;

namespace Arbiter.Controllers;

[ApiController]
[Route("[controller]")]
public class StartGameController : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> PostAsync([FromBody] StartGameRequest request)
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

        try
        {

            if (request.Type.Equals("gameserver", StringComparison.OrdinalIgnoreCase)) {
                var rcc = RCCService.Start(Helper.GetAvailablePort(Configuration.GetIntFlag("DFIntRCCServiceMinPort"), Configuration.GetIntFlag("DFIntRCCServiceMaxPort"), "TCP")); // THIS IS BULLSHIT
                if (rcc == null)
                    return Error.Create(503, "ServiceUnavailable");

                try {
                    await RCCServicePool.WaitForReady(rcc);
                } catch {
                    rcc.Kill();
                    RCCServicePool.Kill(rcc, rcc.Process.Id);
                    return Error.Create(503, "ServiceUnavailable");
                }

                RCCServicePool.RegisterProcess(rcc);

                int raknetPort;
                int publicPort;
                ReverseProxy? proxy = null;

                if (Configuration.GetFlag("FFlagUseReverseProxy"))
                {
                    raknetPort = Helper.GetAvailablePort(Configuration.GetIntFlag("DFIntGameServerMinPort"), Configuration.GetIntFlag("DFIntGameServerMaxPort"), "UDP");
                    int proxyPort = Helper.GetGameServerPort();

                    proxy = new ReverseProxy(proxyPort, raknetPort);
                    proxy.Start();

                    Logger.Debug($"Reverse proxy enabled: public={proxyPort}, internal={raknetPort}");

                    publicPort = proxyPort;
                }
                else
                {
                    raknetPort = Helper.GetGameServerPort();
                    publicPort = raknetPort;

                    Logger.Debug($"Reverse proxy disabled: public={publicPort}");
                }

                // we need to pass on args to gameserver
                args.Insert(0, LuaValue.FromNumber(raknetPort)); // gameserver port
                args.Add(LuaValue.FromNumber(request.Id)); // placeid
                args.Add(LuaValue.FromString(jobId)); // jobId

                if (Configuration.GetFlag("FFlagRCCServiceOnlySpeaksJSON")) {
                    script = Helper.ProcessArguments(script, args);
                }

                /*_ = Task.Run(() =>
                {*/
                await SOAP.Send(
                        port: rcc.Port,
                        script: script,
                        action: "OpenJobEx",
                        jobId: jobId,
                        arguments: args,
                        expirationInSeconds: Configuration.GetIntFlag("FIntGameServerExpirationInSeconds"),
                        cores: Math.Max(1, Health.GetPhysicalCoreCount() / Process.GetProcessesByName(Configuration.GetStringFlag("FStringRCCServiceName")).Length),
                        category: Math.Max(3, 65535)
                    );
                //});

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

                if (Configuration.GetFlag("FFlagRCCServiceOnlySpeaksJSON"))
                    script = Helper.ProcessArguments(script, args);

                var response = await SOAP.Send(
                    port: rcc.Port,
                    script: script,
                    action: "BatchJobEx",
                    jobId: jobId,
                    arguments: args,
                    expirationInSeconds: 30, // half a minute for a render and thats good enough
                    cores: Math.Min(2, Health.GetPhysicalCoreCount()),
                    category: 2
                );

                var rccvalue = response.Value;

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
                RCCServicePool.Kill(rcc, rcc.Process.Id);

                var (mime, ext) = MIME(bytes);

                return File(bytes, mime);
            }
        }
        catch (Exception ex)
        {
            return Error.Create(500, ex.Message);
        }
    }

    private static (string mime, string ext) MIME(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 4) return ("application/octet-stream", "bin");

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            return ("image/png", "png");

        // JPEG/JPG: FF D8 FF
        if (bytes.Length >= 3 &&
            bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return ("image/jpeg", "jpg");

        // OBJ: usually starting with "v ", "vn ", "vt ", "f ", etc
        // Heuristic: treat as text and check common prefixes in first chunk
        {
            int sampleLen = Math.Min(bytes.Length, 512);
            var text = Encoding.UTF8.GetString(bytes, 0, sampleLen).TrimStart();
            if (text.StartsWith("v ") || text.StartsWith("vn ") || text.StartsWith("vt ") ||
                text.StartsWith("f ") || text.StartsWith("o ") || text.StartsWith("g "))
                return ("text/plain", "obj");
        }

        return ("application/octet-stream", "bin");
    }
}

public sealed class StartGameRequest
{
    public string Type { get; set; } = string.Empty;
    public long Id { get; set; }

    // ["string", 67, true]
    public List<object> Arguments { get; set; } = new();
}