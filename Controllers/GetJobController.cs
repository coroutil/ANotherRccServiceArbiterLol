using Microsoft.AspNetCore.Mvc;

namespace Arbiter.Controllers;

[ApiController]
[Route("[controller]")]
public class GetJobController : ControllerBase
{
    [HttpGet]
    public IActionResult Get([FromQuery] string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return Error.Create(400, "BadRequest");

        var job = GameMonitorService.Get(jobId);

        if (job == null)
            return Error.Create(404, "NotFound");

        return Ok(new
        {
            jobId = job.JobId,
            placeId = job.PlaceId,
            port = job.Port,
            soap = job.SOAP,
            pid = job.Pid
        });
    }
}