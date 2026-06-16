using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace Arbiter.Controllers;

[ApiController]
[Route("[controller]")]
public class RenewLeaseController : ControllerBase
{
    [HttpPost]
    public IActionResult Post([FromBody] RenewLeaseRequest request)
    {
        try
        {
            bool success = RenewLease(request.gameId, request.expirationInSeconds);

            if (!success)
                return Error.Create(500, "InternalServerError");

            return Ok();
        }
        catch (Exception ex)
        {
            return Error.Create(500, "InternalServerError");
        }
    }

    private static bool RenewLease(string gameId, int expirationInSeconds)
    {
        using var client = new HttpClient();

        try
        {
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.UseNagleAlgorithm = false;

            var job = GameMonitorService.Get(gameId);

            if (job == null)
                throw new Exception($"{gameId} wasn't found in GMS");

            var soap = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soapenv:Envelope xmlns:soapenv=""http://schemas.xmlsoap.org/soap/envelope/"" xmlns:rob=""http://{Configuration.GetStringFlag("FStringBaseURL")}/"">
  <soapenv:Body>
    <rob:RenewLease>
      <rob:jobID>{gameId}</rob:jobID>
      <rob:expirationInSeconds>{expirationInSeconds}</rob:expirationInSeconds>
    </rob:RenewLease>
  </soapenv:Body>
</soapenv:Envelope>";

            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{job.SOAP}/");
            req.Version = HttpVersion.Version11;
            req.VersionPolicy = HttpVersionPolicy.RequestVersionExact;

            req.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(soap));
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("text/xml") { CharSet = "utf-8" };

            req.Headers.Add("SOAPAction", "RenewLease");
            req.Headers.Host = $"127.0.0.1:{job.SOAP}";
            req.Headers.ConnectionClose = true;

            client.DefaultRequestHeaders.ExpectContinue = false;

            using var resp = client.Send(req);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            throw new Exception($"An unexpected error occurred:\n{ex}");
        }
    }
}

public class RenewLeaseRequest
{
    public string gameId { get; set; }
    public int expirationInSeconds { get; set; }
}