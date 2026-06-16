using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace Arbiter.Controllers;

public record ExecuteScript(
    string gameId,
    string scriptName,
    object[] arguments,
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

        static string BuildArgument(object? arg)
        {
            return arg switch
            {
                string s => $@"
<rob:LuaValue>
  <rob:type>LUA_TSTRING</rob:type>
  <rob:value>{System.Security.SecurityElement.Escape(s)}</rob:value>
</rob:LuaValue>",

                int i => $@"
<rob:LuaValue>
  <rob:type>LUA_TNUMBER</rob:type>
  <rob:value>{i}</rob:value>
</rob:LuaValue>",

                long l => $@"
<rob:LuaValue>
  <rob:type>LUA_TNUMBER</rob:type>
  <rob:value>{l}</rob:value>
</rob:LuaValue>",

                float f => $@"
<rob:LuaValue>
  <rob:type>LUA_TNUMBER</rob:type>
  <rob:value>{f}</rob:value>
</rob:LuaValue>",

                double d => $@"
<rob:LuaValue>
  <rob:type>LUA_TNUMBER</rob:type>
  <rob:value>{d}</rob:value>
</rob:LuaValue>",

                bool b => $@"
<rob:LuaValue>
  <rob:type>LUA_TBOOLEAN</rob:type>
  <rob:value>{b.ToString().ToLowerInvariant()}</rob:value>
</rob:LuaValue>",

                null => @"
<rob:LuaValue>
  <rob:type>LUA_TNIL</rob:type>
  <rob:value></rob:value>
</rob:LuaValue>",

                _ => throw new Exception($"Unsupported argument type: {arg.GetType()}")
            };
        }

        string arguments = string.Empty;

        if (body.arguments?.Length > 0)
        {
            var sb = new StringBuilder();
            sb.Append("<rob:arguments>");

            foreach (var arg in body.arguments)
                sb.Append(BuildArgument(arg));

            sb.Append("</rob:arguments>");
            arguments = sb.ToString();
        }

        try
        {
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.UseNagleAlgorithm = false;

            var baseUrl = Configuration.GetStringFlag("FStringBaseURL");

            var soap = $@"
<soapenv:Envelope xmlns:soapenv=""http://schemas.xmlsoap.org/soap/envelope/"" xmlns:rob=""http://{baseUrl}/"">
  <soapenv:Body>
    <rob:ExecuteEx>
      <rob:jobID>{body.gameId}</rob:jobID>
      <rob:script>
        <rob:name>{body.scriptName}</rob:name>
        <rob:script><![CDATA[
{body.script}
        ]]></rob:script>
        {arguments}
      </rob:script>
    </rob:ExecuteEx>
  </soapenv:Body>
</soapenv:Envelope>";

            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{job.SOAP}/");
            req.Version = HttpVersion.Version11;
            req.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            req.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(soap));
            req.Content.Headers.ContentType =
                new MediaTypeHeaderValue("text/xml")
                {
                    CharSet = "utf-8"
                };

            req.Headers.Add("SOAPAction", "ExecuteScript");
            req.Headers.ConnectionClose = true;

            using var resp = await SOAP.client.SendAsync(req);

            var responseBody = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception(responseBody);

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