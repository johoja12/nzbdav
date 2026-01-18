using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.ResetProviderStats;

[ApiController]
[Route("api/reset-provider-stats")]
public class ResetProviderStatsController(
    DavDatabaseClient dbClient,
    NzbProviderAffinityService affinityService
) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var jobName = Request.Query["jobName"].ToString();

        if (string.IsNullOrEmpty(jobName))
        {
            // Reset all provider stats - clear both in-memory cache AND database
            affinityService.ClearAllStats();

            await dbClient.Ctx.NzbProviderStats
                .ExecuteDeleteAsync()
                .ConfigureAwait(false);

            return Ok(new { message = "All provider stats have been reset", deletedCount = -1 });
        }
        else
        {
            // Reset stats for specific job - clear both in-memory cache AND database
            affinityService.ClearJobStats(jobName);

            var deletedCount = await dbClient.Ctx.NzbProviderStats
                .Where(x => x.JobName == jobName)
                .ExecuteDeleteAsync()
                .ConfigureAwait(false);

            return Ok(new { message = $"Provider stats reset for job: {jobName}", deletedCount });
        }
    }
}
