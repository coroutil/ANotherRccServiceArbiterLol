using Microsoft.AspNetCore.Mvc;

namespace Arbiter.Controllers;

[ApiController]
[Route("[controller]")]
public class GetStatsController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        var response = new
        {
            PhysicalMemoryGigabytesUsage = MathF.Round(Health.GetRAM(), 1),
            availablePhysicalMemoryGigabytes = MathF.Round(Health.AvailablePhysicalMemoryGigabytes, 2),
            totalPhysicalMemoryGigabytes = MathF.Round(Health.TotalPhysicalMemoryGigabytes, 2),
            cpuUsage = MathF.Round(Health.CpuUsage, 2),
            downloadSpeedKilobytesPerSecond = MathF.Round(Health.DownloadSpeedKilobytesPerSecond, 2),
            uploadSpeedKilobytesPerSecond = MathF.Round(Health.UploadSpeedKilobytesPerSecond, 2),
            logicalProcessorCount = Health.LogicalProcessorCount,
            processorCount = Health.ProcessorCount,
            rccServiceProcesses = Health.RccServiceProcesses,
            rccVersion = Health.RccVersion
        };

        return Ok(response);
    }
}