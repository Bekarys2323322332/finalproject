using CvManager.Data;
using CvManager.Models;
using CvManager.Models.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CvManager.Controllers;

// The header search. Every query goes through Postgres full-text search against
// the generated tsvector columns, which are GIN indexed - no LIKE '%term%' scans
// and no loading of rows just to filter them in memory.
[AllowAnonymous]
public class SearchController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;

    public SearchController(ApplicationDbContext db, UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _userManager = userManager;
    }

    public async Task<IActionResult> Index(string? q, string? scope)
    {
        var model = new SearchViewModel { Query = q, Scope = scope };

        if (string.IsNullOrWhiteSpace(q))
        {
            return View(model);
        }

        var canSeeCvs = User.IsInRole(Roles.Recruiter) || User.IsInRole(Roles.Admin);
        model.CanSeeCvs = canSeeCvs;

        // The tag comparison uses a normalised copy of the term, because tags are
        // stored lowercase.
        var tagName = q.Trim().ToLowerInvariant();

        // Note: EF.Functions.WebSearchToTsQuery has to appear *inside* each query
        // expression. Assigning it to a variable first makes EF treat it as a
        // client-side call and it throws.

        if (scope != "cvs")
        {
            model.Positions = await _db.Positions
                .Where(p => p.SearchVector!.Matches(EF.Functions.WebSearchToTsQuery("english", q))
                            || p.ProjectTags.Any(pt => pt.Tag!.Name == tagName))
                .OrderByDescending(p => p.UpdatedAt)
                .Take(25)
                .Select(p => new PositionListItem
                {
                    Id = p.Id,
                    Title = p.Title,
                    Company = p.Company,
                    Level = p.Level,
                    IsPublic = p.IsPublic,
                    CvCount = p.Cvs.Count,
                    UpdatedAt = p.UpdatedAt
                })
                .ToListAsync();
        }

        if (canSeeCvs)
        {
            var isAdmin = User.IsInRole(Roles.Admin);

            // CVs are matched through their candidate's answers (a name, a text
            // answer) and through the tags of the projects they include.
            var cvQuery = _db.Cvs.AsQueryable();
            if (!isAdmin)
            {
                cvQuery = cvQuery.Where(c => c.State == CvState.Published);
            }

            model.Cvs = await cvQuery
                .Where(c =>
                    _db.AttributeValues.Any(v => v.UserId == c.UserId
                        && v.SearchVector!.Matches(EF.Functions.WebSearchToTsQuery("english", q)))
                    || c.Position!.SearchVector!.Matches(EF.Functions.WebSearchToTsQuery("english", q))
                    || _db.Projects.Any(p => p.UserId == c.UserId
                                             && p.ProjectTags.Any(pt => pt.Tag!.Name == tagName)))
                .OrderByDescending(c => c.UpdatedAt)
                .Take(25)
                .Select(c => new CvListItem
                {
                    Id = c.Id,
                    PositionId = c.PositionId,
                    PositionTitle = c.Position!.Title,
                    Email = c.User!.Email,
                    State = c.State,
                    Likes = c.Likes.Count,
                    UpdatedAt = c.UpdatedAt,
                    FirstName = _db.AttributeValues
                        .Where(v => v.UserId == c.UserId && v.AttributeId == ApplicationDbContext.FirstNameAttributeId)
                        .Select(v => v.ValueString).FirstOrDefault(),
                    LastName = _db.AttributeValues
                        .Where(v => v.UserId == c.UserId && v.AttributeId == ApplicationDbContext.LastNameAttributeId)
                        .Select(v => v.ValueString).FirstOrDefault()
                })
                .ToListAsync();
        }

        // A candidate searching finds their own projects; recruiters see none,
        // because projects reach them through CVs.
        var userId = _userManager.GetUserId(User);
        if (userId is not null && !canSeeCvs)
        {
            model.MyProjects = await _db.Projects
                .Where(p => p.UserId == userId
                            && (p.SearchVector!.Matches(EF.Functions.WebSearchToTsQuery("english", q))
                                || p.ProjectTags.Any(pt => pt.Tag!.Name == tagName)))
                .OrderByDescending(p => p.StartDate)
                .Take(25)
                .Include(p => p.ProjectTags).ThenInclude(pt => pt.Tag)
                .AsNoTracking()
                .ToListAsync();
        }

        return View(model);
    }
}
