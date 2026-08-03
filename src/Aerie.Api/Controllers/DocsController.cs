using Aerie.Api.Models.Docs;
using Aerie.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Aerie.Api.Controllers;

/// <summary>Backs the docs browser app (apps/docs): lists and serves the markdown files under /docs.</summary>
[ApiController]
[Route("api/docs")]
public class DocsController(IDocsService docs) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<DocSummary>> GetAll() => Ok(docs.GetAll());

    [HttpGet("{slug}")]
    public async Task<IActionResult> GetContent(string slug, CancellationToken ct)
    {
        var content = await docs.GetContentAsync(slug, ct);
        return content is null ? NotFound() : Content(content, "text/markdown");
    }
}
