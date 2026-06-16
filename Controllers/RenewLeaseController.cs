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

            var response = SOAP.Send(
                job.SOAP,
                "RenewLease",
                string.Empty,
                "BatchJobEx",
                out var rccvalue,
                gameId,
                expirationInSeconds: 30
            );

            return true;
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