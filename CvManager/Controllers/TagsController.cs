using CvManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CvManager.Controllers;

// Feeds the tag input's autocomplete. Tags are shared, so any signed-in user may
// read the list; they are created implicitly when a project or position is saved.
[Authorize]
public class TagsController : Controller
{
    private readonly TagService _tags;

    public TagsController(TagService tags)
    {
        _tags = tags;
    }

    [HttpGet]
    public async Task<IActionResult> Suggest(string? prefix)
    {
        return Json(await _tags.SuggestAsync(prefix ?? string.Empty));
    }
}
