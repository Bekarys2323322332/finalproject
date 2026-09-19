using CvManager.Data;
using CvManager.Models;
using CvManager.Models.ViewModels;
using CvManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CvManager.Controllers;

// The personal profile: Me, Info, Projects and CVs.
// Only the owner and admins may open it. Recruiters do not get here at all -
// they read candidate data through published CVs, and reach this controller only
// through the read-only View action.
[Authorize]
public class ProfileController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly PositionAccessService _access;
    private readonly TagService _tags;
    private readonly MarkdownRenderer _markdown;
    private readonly BadgeService _badges;
    private readonly UserManager<ApplicationUser> _userManager;

    public ProfileController(ApplicationDbContext db, PositionAccessService access, TagService tags,
        MarkdownRenderer markdown, BadgeService badges, UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _access = access;
        _tags = tags;
        _markdown = markdown;
        _badges = badges;
        _userManager = userManager;
    }

    public Task<IActionResult> Index() => ShowProfile(_userManager.GetUserId(User)!, editable: true);

    // Read-only profile, used by the link on a discussion post. Recruiters and
    // admins only.
    [Authorize(Roles = Roles.RecruiterOrAdmin)]
    public async Task<IActionResult> View(string userId)
    {
        // An admin follows the same link but gets the editable page, since they
        // act as the owner of every personal page.
        return await ShowProfile(userId, editable: User.IsInRole(Roles.Admin));
    }

    // Admins edit a candidate's profile through the same page as the owner.
    [Authorize(Roles = Roles.Admin)]
    public Task<IActionResult> Edit(string userId) => ShowProfile(userId, editable: true);

    private async Task<IActionResult> ShowProfile(string userId, bool editable)
    {
        var owner = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (owner is null)
        {
            return NotFound();
        }

        // Everything this user has answered, in one query, with the dropdown
        // options they may choose from.
        var values = await _db.AttributeValues
            .Where(v => v.UserId == userId)
            .Include(v => v.Attribute!).ThenInclude(a => a.Options.OrderBy(o => o.SortOrder))
            .Include(v => v.ValueOption)
            .AsNoTracking()
            .ToListAsync();

        // The built-in attributes always exist even if this user has no row yet,
        // so they are read from the library and matched against the answers.
        var systemAttributes = await _db.LibraryAttributes
            .Where(a => a.IsSystem)
            .OrderBy(a => a.SortOrder)
            .Include(a => a.Options)
            .AsNoTracking()
            .ToListAsync();

        var me = systemAttributes
            .Select(a => new CvAttributeRow
            {
                Attribute = a,
                Value = values.FirstOrDefault(v => v.AttributeId == a.Id)
            })
            .ToList();

        var info = values
            .Where(v => v.Attribute is { IsSystem: false })
            .OrderBy(v => v.Attribute!.Name)
            .Select(v => new CvAttributeRow { Attribute = v.Attribute!, Value = v })
            .ToList();

        var projects = await _db.Projects
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.StartDate)
            .Include(p => p.ProjectTags).ThenInclude(pt => pt.Tag)
            .AsNoTracking()
            .ToListAsync();

        // Which of this candidate's CVs are still reachable. One query for the
        // accessible position ids, then matched in memory.
        var accessiblePositions = (await _access.AccessibleTo(userId)
                .Select(p => p.Id)
                .ToListAsync())
            .ToHashSet();

        var cvs = await _db.Cvs
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new
            {
                c.Id,
                c.PositionId,
                PositionTitle = c.Position!.Title,
                c.State,
                Likes = c.Likes.Count,
                c.UpdatedAt
            })
            .AsNoTracking()
            .ToListAsync();

        ViewData["Markdown"] = _markdown;

        return View("Index", new ProfileViewModel
        {
            Owner = owner,
            MeAttributes = me,
            InfoAttributes = info,
            Projects = projects,
            Categories = await _db.AttributeCategories.AsNoTracking().OrderBy(c => c.Name).ToListAsync(),
            CanEdit = editable,
            Cvs = cvs.Select(c => new ProfileCvItem
            {
                Id = c.Id,
                PositionTitle = c.PositionTitle,
                State = c.State,
                Likes = c.Likes,
                UpdatedAt = c.UpdatedAt,
                Hidden = !accessiblePositions.Contains(c.PositionId)
            }).ToList()
        });
    }

    // Optional extra: the achievements panel as a downloadable SVG. Served as a
    // file so it can be dropped into a README or a portfolio page.
    public async Task<IActionResult> Badges(string? userId, bool download = false)
    {
        var target = string.IsNullOrWhiteSpace(userId) ? _userManager.GetUserId(User)! : userId!;

        // Only the owner and admins may ask for somebody's panel.
        if (target != _userManager.GetUserId(User) && !User.IsInRole(Roles.Admin))
        {
            return Forbid();
        }

        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == target);
        if (user is null)
        {
            return NotFound();
        }

        var name = await _db.AttributeValues
            .Where(v => v.UserId == target && v.AttributeId == ApplicationDbContext.FirstNameAttributeId)
            .Select(v => v.ValueString)
            .FirstOrDefaultAsync();

        var badges = await _badges.ForUserAsync(target);
        var svg = BadgeService.RenderSvg(name ?? user.Email ?? "Candidate", badges);
        var bytes = System.Text.Encoding.UTF8.GetBytes(svg);

        return download
            ? File(bytes, "image/svg+xml", "cv-manager-badges.svg")
            : File(bytes, "image/svg+xml");
    }

    // "Candidates may add or remove attributes from the library." Adding creates
    // an empty value row, which is what puts the attribute on their page.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddAttribute(int attributeId, string? ownerId)
    {
        var userId = TargetUser(ownerId);

        var exists = await _db.AttributeValues
            .AnyAsync(v => v.UserId == userId && v.AttributeId == attributeId);

        if (!exists)
        {
            _db.AttributeValues.Add(new AttributeValue
            {
                UserId = userId,
                AttributeId = attributeId,
                HasValue = false
            });

            var attribute = await _db.LibraryAttributes.FirstOrDefaultAsync(a => a.Id == attributeId);
            if (attribute is not null)
            {
                attribute.LastUsedAt = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync();
        }

        return RedirectToProfile(ownerId);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveAttributes(int[] attributeIds, string? ownerId)
    {
        var userId = TargetUser(ownerId);

        // Built-in attributes cannot be dropped - they always exist.
        var rows = await _db.AttributeValues
            .Where(v => v.UserId == userId && attributeIds.Contains(v.AttributeId) && !v.Attribute!.IsSystem)
            .ToListAsync();

        _db.AttributeValues.RemoveRange(rows);
        await _db.SaveChangesAsync();

        return RedirectToProfile(ownerId);
    }

    [HttpGet]
    public async Task<IActionResult> Project(int? id, string? ownerId)
    {
        var userId = TargetUser(ownerId);

        if (id is null)
        {
            return View(new ProjectEditViewModel { OwnerId = ownerId });
        }

        var project = await _db.Projects
            .Include(p => p.ProjectTags).ThenInclude(pt => pt.Tag)
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);

        if (project is null)
        {
            return NotFound();
        }

        return View(new ProjectEditViewModel
        {
            Id = project.Id,
            Name = project.Name,
            StartDate = project.StartDate,
            EndDate = project.EndDate,
            DescriptionMarkdown = project.DescriptionMarkdown,
            Tags = string.Join(", ", project.ProjectTags.Select(pt => pt.Tag!.Name)),
            Version = project.Version,
            OwnerId = ownerId
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveProject(ProjectEditViewModel model)
    {
        var userId = TargetUser(model.OwnerId);

        if (!ModelState.IsValid)
        {
            return View("Project", model);
        }

        Project project;
        if (model.Id == 0)
        {
            project = new Project { UserId = userId };
            _db.Projects.Add(project);
        }
        else
        {
            var existing = await _db.Projects
                .Include(p => p.ProjectTags)
                .FirstOrDefaultAsync(p => p.Id == model.Id && p.UserId == userId);

            if (existing is null)
            {
                return NotFound();
            }

            project = existing;
            _db.Entry(project).Property(p => p.Version).OriginalValue = model.Version;
        }

        project.Name = model.Name.Trim();
        project.StartDate = model.StartDate;
        project.EndDate = model.EndDate;
        project.DescriptionMarkdown = model.DescriptionMarkdown;

        // Tags are created on first use; GetOrCreateAsync resolves the whole list
        // in one query rather than one lookup per tag.
        var names = (model.Tags ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tags = await _tags.GetOrCreateAsync(names);

        project.ProjectTags.Clear();
        foreach (var tag in tags)
        {
            project.ProjectTags.Add(new ProjectTag { TagId = tag.Id });
        }

        try
        {
            await _db.SaveChangesAsync();
            TempData["Status"] = "Project saved.";
        }
        catch (DbUpdateConcurrencyException)
        {
            ModelState.AddModelError(string.Empty,
                "This project was changed somewhere else while you were editing it. Reopen it and apply your changes again.");
            return View("Project", model);
        }

        return RedirectToProfile(model.OwnerId);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteProjects(int[] ids, string? ownerId)
    {
        var userId = TargetUser(ownerId);

        var projects = await _db.Projects
            .Where(p => ids.Contains(p.Id) && p.UserId == userId)
            .ToListAsync();

        _db.Projects.RemoveRange(projects);
        await _db.SaveChangesAsync();

        TempData["Status"] = $"Deleted {projects.Count} project(s).";
        return RedirectToProfile(ownerId);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult EditProjectSelected(int[] ids, string? ownerId)
    {
        return ids.Length == 1
            ? RedirectToAction(nameof(Project), new { id = ids[0], ownerId })
            : RedirectToProfile(ownerId);
    }

    // Admins may act on another user's profile; everybody else is pinned to
    // their own id no matter what the form says.
    private string TargetUser(string? ownerId)
    {
        var me = _userManager.GetUserId(User)!;
        return User.IsInRole(Roles.Admin) && !string.IsNullOrWhiteSpace(ownerId) ? ownerId! : me;
    }

    private IActionResult RedirectToProfile(string? ownerId)
    {
        return string.IsNullOrWhiteSpace(ownerId) || !User.IsInRole(Roles.Admin)
            ? RedirectToAction(nameof(Index))
            : RedirectToAction(nameof(Edit), new { userId = ownerId });
    }
}
