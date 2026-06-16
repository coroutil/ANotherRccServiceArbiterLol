using Microsoft.AspNetCore.Mvc;

namespace Arbiter.Controllers;

[ApiController]
[Route("[controller]")]
public class GetAllJobsController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        var jobs = GameMonitorService.GetAll()
            .Select(job => new
            {
                jobId = job.JobId,
                placeId = job.PlaceId,
                port = job.Port,
                soap = job.SOAP,
                pid = job.Pid
            });

        return Ok(jobs);
    }
}