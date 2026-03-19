using AccessCity.API.Models;
using AccessCity.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AccessCity.API.Controllers;

[Authorize]
[ApiController]
[Route("api/admin/osm")]
public class AdminOsmController : ControllerBase
{
    private readonly IOsmImportService _osmImportService;

    public AdminOsmController(IOsmImportService osmImportService)
    {
        _osmImportService = osmImportService;
    }

    [HttpPost("import")]
    [ProducesResponseType(typeof(OsmImportResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<OsmImportResult>> Import(CancellationToken cancellationToken)
    {
        var result = await _osmImportService.ImportConfiguredAsync(cancellationToken);
        return Ok(result);
    }
}
