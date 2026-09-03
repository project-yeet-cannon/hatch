using Aerie.Api.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// Projects, which in Hatch are key namespaces rather than containers - see
/// <see cref="EfHatchProject"/>. Four verbs, and the interesting half of them
/// is what they refuse.
/// </summary>
[ApiController]
[Route("api/hatch/projects")]
[RequireAdmin]
public class ProjectsController(HatchContext db, TimeProvider time) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProjectDto>>> GetProjects(CancellationToken ct)
    {
        var projects = await db.Projects.AsNoTracking()
            .OrderBy(p => p.Key)
            .Select(p => new ProjectDto(p.Id, p.Key, p.Name, p.Issues.Count, p.CreatedAt))
            .ToListAsync(ct);

        return projects;
    }

    [HttpPost]
    public async Task<ActionResult<ProjectDto>> CreateProject(ProjectCreateRequest request, CancellationToken ct)
    {
        // Upper-cased on the way in rather than refused: keys are shouted in
        // storage, and typing one in lower case is not a mistake worth a 400.
        var key = request.Key?.Trim().ToUpperInvariant();
        if (!EfHatchProject.IsValidKey(key))
            return BadRequest($"a project key is two to six letters or digits starting with a letter - not \"{request.Key}\"");

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name)) return BadRequest("a project needs a name");
        if (name.Length > EfHatchProject.MaxNameLength)
            return BadRequest($"a project name is at most {EfHatchProject.MaxNameLength} characters");

        // Checked here for the sentence, and enforced by a unique index
        // underneath for the race - the message is the point of doing it twice.
        if (await db.Projects.AnyAsync(p => p.Key == key, ct))
            return Conflict($"{key} is already taken");

        var project = new EfHatchProject { Key = key!, Name = name, CreatedAt = time.GetUtcNow() };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetProjects), new ProjectDto(project.Id, project.Key, project.Name, 0, project.CreatedAt));
    }

    /// <summary>
    /// Renames a project. The key is not offered, and that is the design rather
    /// than an omission: it is written into commit messages, branch names, and
    /// chat logs this database has never seen and cannot rewrite, so a rename
    /// would be a feature that half works.
    /// </summary>
    [HttpPatch("{id:int}")]
    public async Task<ActionResult<ProjectDto>> PatchProject(int id, ProjectPatchRequest request, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (project is null) return NotFound();

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name)) return BadRequest("a project needs a name");
        if (name.Length > EfHatchProject.MaxNameLength)
            return BadRequest($"a project name is at most {EfHatchProject.MaxNameLength} characters");

        project.Name = name;
        await db.SaveChangesAsync(ct);

        var count = await db.Issues.CountAsync(i => i.ProjectId == id, ct);
        return new ProjectDto(project.Id, project.Key, project.Name, count, project.CreatedAt);
    }

    /// <summary>
    /// Deletes an empty project. Never a full one: the issues would have to go
    /// somewhere, and every answer to "where" is worse than making the operator
    /// deal with them first.
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteProject(int id, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (project is null) return NotFound();

        var count = await db.Issues.CountAsync(i => i.ProjectId == id, ct);
        if (count > 0) return Conflict($"{project.Key} still has {count} issue{(count == 1 ? "" : "s")} in it");

        db.Projects.Remove(project);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
