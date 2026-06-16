using System.Net;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace Arbiter;

public static class SOAP
{
    public static readonly HttpClient client = new();

    private static string BuildArguments(List<LuaValue>? arguments)
    {
        if (arguments == null || arguments.Count == 0)
            return string.Empty;

        var xml = new StringBuilder();

        xml.AppendLine("<rob:arguments>");

        foreach (var a in arguments)
        {
            xml.AppendLine("<rob:LuaValue>");

            switch (a.Kind)
            {
                case LuaValue.ValueKind.String:
                    xml.AppendLine("<rob:type>LUA_TSTRING</rob:type>");
                    xml.AppendLine(
                        $"<rob:value>{SecurityElement.Escape(a.StringValue ?? "")}</rob:value>");
                    break;

                case LuaValue.ValueKind.Number:
                    xml.AppendLine("<rob:type>LUA_TNUMBER</rob:type>");
                    xml.AppendLine(
                        $"<rob:value>{a.NumberValue?.ToString(System.Globalization.CultureInfo.InvariantCulture)}</rob:value>");
                    break;

                case LuaValue.ValueKind.Boolean:
                    xml.AppendLine("<rob:type>LUA_TBOOLEAN</rob:type>");
                    xml.AppendLine(
                        $"<rob:value>{(a.BooleanValue == true ? "true" : "false")}</rob:value>");
                    break;
            }

            xml.AppendLine("</rob:LuaValue>");
        }

        xml.AppendLine("</rob:arguments>");

        return xml.ToString();
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

            "Execute" => $@"
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
<soapenv:Envelope xmlns:soapenv=""http://schemas.xmlsoap.org/soap/envelope/"" xmlns:rob=""http://{baseUrl}/"">
  <soapenv:Header />
  <soapenv:Body>
    {body}
  </soapenv:Body>
</soapenv:Envelope>";
    }

    public static string Send(int port, string jobType, string script, string action, out string? rccvalue, string? jobId = null, List<LuaValue>? arguments = null, int expirationInSeconds = 120, int cores = 1, int category = 1)
    {
        rccvalue = null;

        var xml = BuildEnvelope(jobType, script, arguments, jobId, expirationInSeconds, cores);
        Console.WriteLine(xml);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/");

        ServicePointManager.Expect100Continue = false;
        ServicePointManager.UseNagleAlgorithm = false;

        req.Version = HttpVersion.Version11;
        req.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        req.Content = new StringContent(xml, Encoding.UTF8, "text/xml");

        req.Headers.Add("SOAPAction", action);
        req.Headers.ConnectionClose = true;

        var resp = client.SendAsync(req).GetAwaiter().GetResult();
        var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        if (!resp.IsSuccessStatusCode)
            throw new Exception(body);

        if (category == 2)
        {
            var doc = XDocument.Parse(body);

            var faultstring = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultstring");
            if (faultstring != null)
                throw new Exception((string?)faultstring);

            var value = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "value");
            if (value == null)
                throw new Exception($"No value was found in response. Unsupported RCCService version? {body}");

            Helper.fixitup(value.Value.Trim(), out rccvalue);
        }

        return body;
    }
}