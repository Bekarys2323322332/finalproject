using CvManager.Data;
using CvManager.Models;
using CvManager.Models.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CvManager.Controllers;

// The main page. Visible to everybody, including visitors who are not signed in -
// they get the position list and the statistics, which is what the spec allows.
public class HomeController : Controller
{
    private readonly ApplicationDbContext _db;

    public HomeController(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<IActionResult> Index()
    {
        var since = DateTime.UtcNow.AddHours(-24);

        // Each block below is its own query, but every one is a projection or an
        // aggregate - nothing loads whole entities it does not need, and the
        // counts are done by the database rather than by counting lists in C#.
        var latest = await _db.Positions
            .OrderByDescending(p => p.UpdatedAt)
            .Take(10)
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

        // "Top 5 positions ranked by the number of submitted CVs."
        var popular = await _db.Positions
            .OrderByDescending(p => p.Cvs.Count)
            .ThenByDescending(p => p.UpdatedAt)
            .Take(5)
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

        // Tag cloud. The weight is how many projects carry the tag; the view
        // turns that into a font size.
        var tags = await _db.Tags
            .Select(t => new TagCloudItem { Name = t.Name, Weight = t.ProjectTags.Count })
            .Where(t => t.Weight > 0)
            .OrderByDescending(t => t.Weight)
            .Take(40)
            .ToListAsync();

        var model = new HomeViewModel
        {
            LatestPositions = latest,
            PopularPositions = popular,
            Tags = tags,
            CvsLast24Hours = await _db.Cvs.CountAsync(c => c.CreatedAt >= since),
            TotalPositions = await _db.Positions.CountAsync(),
            TotalCvs = await _db.Cvs.CountAsync(),
            PublishedCvs = await _db.Cvs.CountAsync(c => c.State == CvState.Published),
            TotalCandidates = await _db.Users.CountAsync(u => _db.UserRoles
                .Any(ur => ur.UserId == u.Id && _db.Roles
                    .Any(r => r.Id == ur.RoleId && r.Name == Roles.Candidate))),
            TotalRecruiters = await _db.Users.CountAsync(u => _db.UserRoles
                .Any(ur => ur.UserId == u.Id && _db.Roles
                    .Any(r => r.Id == ur.RoleId && r.Name == Roles.Recruiter)))
        };

        return View(model);
    }

    public IActionResult Privacy() => View();

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel
        {
            RequestId = System.Diagnostics.Activity.Current?.Id ?? HttpContext.TraceIdentifier
        });
    }
}
