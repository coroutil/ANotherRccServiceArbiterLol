using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Arbiter;

public static class SOAP
{
    public static readonly HttpClient client = new();
    private static readonly string Namespace = $"http://{Configuration.GetStringFlag("FStringBaseURL")}/";

    // AHH!!! -Seymour
    private static string BuildArguments(List<LuaValue>? arguments)
    {
        if (arguments == null || arguments.Count == 0)
            return string.Empty;

        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = true
        };

        using var sw = new StringWriter();
        using var writer = XmlWriter.Create(sw, settings);

        writer.WriteStartElement("rob", "arguments", Namespace);

        foreach (var a in arguments)
        {
            writer.WriteStartElement("rob", "LuaValue", Namespace);

            switch (a.Kind)
            {
                case LuaValue.ValueKind.String:
                    writer.WriteElementString("rob", "type", Namespace, "LUA_TSTRING");
                    writer.WriteElementString("rob", "value", Namespace, a.StringValue ?? "");
                    break;

                case LuaValue.ValueKind.Number:
                    writer.WriteElementString("rob", "type", Namespace, "LUA_TNUMBER");
                    writer.WriteElementString("rob", "value", Namespace,
                        a.NumberValue?.ToString(CultureInfo.InvariantCulture) ?? "");
                    break;

                case LuaValue.ValueKind.Boolean:
                    writer.WriteElementString("rob", "type", Namespace, "LUA_TBOOLEAN");
                    writer.WriteElementString("rob", "value", Namespace,
                        a.BooleanValue == true ? "true" : "false");
                    break;
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.Flush();

        return sw.ToString();
    }

    private static string BuildEnvelope(string jobType, string script, List<LuaValue>? arguments = null, string? jobId = null, int expirationInSeconds = 120, int cores = 1, bool rendermode = false)
    {
        var baseUrl = Configuration.GetStringFlag("FStringBaseURL");

        string body = jobType switch
        {
            "OpenJobEx" or "BatchJobEx" => $@"
<rob:{jobType}>
  <rob:job>
    <rob:id>{jobId}</rob:id>
    <rob:expirationInSeconds>{expirationInSeconds}</rob:expirationInSeconds>
    <rob:cores>{cores}</rob:cores>
  </rob:job>

  <rob:script>
    <rob:name>Script</rob:name>
    <rob:script><![CDATA[
{script}
    ]]></rob:script>

{BuildArguments(arguments)}

  </rob:script>
</rob:{jobType}>",

            "ExecuteScript" => $@"
<rob:Execute>
  <rob:jobID>{jobId}</rob:jobID>

  <rob:script>
    <rob:name>Script</rob:name>
    <rob:script><![CDATA[
{script}
    ]]></rob:script>

{BuildArguments(arguments)}

  </rob:script>
</rob:Execute>",

            "RenewLease" => $@"
<rob:RenewLease>
  <rob:jobID>{jobId}</rob:jobID>
  <rob:expirationInSeconds>{expirationInSeconds}</rob:expirationInSeconds>
</rob:RenewLease>",

            _ => throw new ArgumentException($"Unknown job type '{jobType}'")
        };

        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<soapenv:Envelope xmlns:soapenv=""http://schemas.xmlsoap.org/soap/envelope/"" xmlns:rob=""{Namespace}"">
  <soapenv:Header />
  <soapenv:Body>
    {body}
  </soapenv:Body>
</soapenv:Envelope>";
    }

    public static async Task<SOAPResult> Send(int port, string jobType, string script, string action, int expirationInSeconds = 120, int cores = 1, int category = 1, string? jobId = null, List<LuaValue>? arguments = null, CancellationToken cancellationToken = default)
    {
        var result = new SOAPResult();

        var xml = BuildEnvelope(jobType, script, arguments, jobId, expirationInSeconds, cores);
        Console.WriteLine(xml);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/");

        req.Version = HttpVersion.Version11;
        req.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        req.Content = new StringContent(xml, Encoding.UTF8, "text/xml");
        req.Headers.Add("SOAPAction", action);
        req.Headers.ConnectionClose = true;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(expirationInSeconds));

        try
        {
            using var resp = await client.SendAsync(req, timeoutCts.Token);
            var body = await resp.Content.ReadAsStringAsync();

            result.Body = body;

            if (!resp.IsSuccessStatusCode)
                throw new Exception(body);

            if (category == 2)
            {
                var doc = XDocument.Parse(body);

                var faultstring = doc.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "faultstring");

                if (faultstring != null)
                {
                    var job = GameMonitorService.GetByPort(port);

                    if (job != null)
                    {
                        ReverseProxy.Stop(job.Port);

                        try
                        {
                            var process = Process.GetProcessById((int)job.Pid);
                            process.Kill(true);
                        }
                        catch { }

                        RCCServicePool.Kill(job);
                    }
                    var message = string.Concat(faultstring.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
                    throw new Exception(string.IsNullOrWhiteSpace(message) ? "FATAL ERROR IN SOAP" : message);
                }

                var value = doc.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "value");

                if (value == null)
                    throw new Exception($"No value was found. RCCService version mismatch? {body}");

                result.Value = Helper.fixitup(value.Value.Trim());
            }

            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // okay what the fuck some probably huge error just happend, shut down the rcc, since thats our only huge concern
            var job = GameMonitorService.GetByPort(port);

            if (job != null)
            {
                GameMonitorService.Remove(job.JobId);
                ReverseProxy.Stop(job.Port);

                try
                {
                    var process = Process.GetProcessById((int)job.Pid);
                    process.Kill(true);
                }
                catch { }

                RCCServicePool.Kill(job);
            }

            throw new TimeoutException("RCCService timed out");
        }
    }
}

public class SOAPResult
{
    public string Body { get; set; } = "";
    public string? Value { get; set; }
}