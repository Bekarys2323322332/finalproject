using CvManager.Data;
using CvManager.Models;
using CvManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CvManager.Controllers;

// Killer feature #3: the generated CV.
// Creating one stores almost nothing - just "this candidate applied to this
// position" plus a state. Everything shown is looked up from the candidate's
// attribute values and projects when the page is rendered, by CvBuilder.
[Authorize]
public class CvsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly CvBuilder _builder;
    private readonly PositionAccessService _access;
    private readonly MarkdownRenderer _markdown;
    private readonly CvPdfService _pdf;
    private readonly UserManager<ApplicationUser> _userManager;

    public CvsController(ApplicationDbContext db, CvBuilder builder, PositionAccessService access,
        MarkdownRenderer markdown, CvPdfService pdf, UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _builder = builder;
        _access = access;
        _markdown = markdown;
        _pdf = pdf;
        _userManager = userManager;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(int positionId)
    {
        var userId = _userManager.GetUserId(User)!;

        // The access rules are checked on the server, not just hidden in the UI.
        if (!await _access.CanAccessAsync(positionId, userId))
        {
            TempData["Error"] = "You do not meet the access rules for this position.";
            return RedirectToAction("Details", "Positions", new { id = positionId });
        }

        var existing = await _db.Cvs
            .FirstOrDefaultAsync(c => c.PositionId == positionId && c.UserId == userId);

        if (existing is not null)
        {
            // One CV per position per candidate, so reopen the one they have.
            return RedirectToAction(nameof(Details), new { id = existing.Id });
        }

        var cv = new Cv { PositionId = positionId, UserId = userId };
        _db.Cvs.Add(cv);
        await _db.SaveChangesAsync();

        // The attributes the position asks for are added to the candidate's
        // profile as empty rows, which is how an attribute they never filled in
        // shows up on both the CV and their profile.
        await _builder.EnsureAttributeRowsAsync(positionId, userId);

        return RedirectToAction(nameof(Details), new { id = cv.Id });
    }

    public async Task<IActionResult> Details(int id)
    {
        var cv = await _db.Cvs.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (cv is null)
        {
            return NotFound();
        }

        var userId = _userManager.GetUserId(User)!;
        var isAdmin = User.IsInRole(Roles.Admin);
        var isRecruiter = User.IsInRole(Roles.Recruiter);
        var isOwner = cv.UserId == userId;

        // Admins act as the owner of any page, so they get the editable view.
        var canEdit = isOwner || isAdmin;

        if (!canEdit && !isRecruiter)
        {
            // Candidates may only see their own CVs.
            return Forbid();
        }

        if (isRecruiter && !isAdmin && cv.State != CvState.Published)
        {
            // A draft is not visible to recruiters until it is published.
            return NotFound();
        }

        // "If candidates lose the access, the filled out CVs are hidden." The row
        // survives - only the UI hides it, because the content is not stored in
        // the CV anyway.
        if (isOwner && !isAdmin && !await _access.CanAccessAsync(cv.PositionId, cv.UserId))
        {
            TempData["Error"] = "This CV is hidden because you no longer meet the position's access rules.";
            return RedirectToAction("Index", "Profile");
        }

        var model = await _builder.BuildAsync(id, userId, canEdit);
        if (model is null)
        {
            return NotFound();
        }

        ViewData["Markdown"] = _markdown;
        return View(model);
    }

    // Optional extra: the CV as a printable PDF carrying a QR code back to this
    // page. Same permission rules as viewing it.
    public async Task<IActionResult> Pdf(int id)
    {
        var cv = await _db.Cvs.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (cv is null)
        {
            return NotFound();
        }

        var userId = _userManager.GetUserId(User)!;
        var isAdmin = User.IsInRole(Roles.Admin);
        var isRecruiter = User.IsInRole(Roles.Recruiter);
        var isOwner = cv.UserId == userId;

        if (!isOwner && !isAdmin && !isRecruiter)
        {
            return Forbid();
        }

        if (isRecruiter && !isAdmin && cv.State != CvState.Published)
        {
            return NotFound();
        }

        var model = await _builder.BuildAsync(id, userId, canEdit: false);
        if (model is null)
        {
            return NotFound();
        }

        // Absolute URL, because the QR code is scanned from paper.
        var url = Url.Action(nameof(Details), "Cvs", new { id }, Request.Scheme)!;
        var bytes = _pdf.Render(model, url);

        var fileName = $"cv-{model.FullName}-{model.Position.Title}.pdf"
            .Replace(' ', '-')
            .Replace('/', '-');

        return File(bytes, "application/pdf", fileName);
    }

    // Publishing is what makes a CV visible to recruiters, and it is only
    // allowed once every attribute the position asks for has a value.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Publish(int id, int version)
    {
        var cv = await _db.Cvs.FirstOrDefaultAsync(c => c.Id == id);
        if (cv is null)
        {
            return NotFound();
        }

        var userId = _userManager.GetUserId(User)!;
        if (cv.UserId != userId && !User.IsInRole(Roles.Admin))
        {
            return Forbid();
        }

        var model = await _builder.BuildAsync(id, userId, canEdit: true);
        if (model is null)
        {
            return NotFound();
        }

        if (!model.IsComplete)
        {
            TempData["Error"] = "Fill in every highlighted field before publishing.";
            return RedirectToAction(nameof(Details), new { id });
        }

        _db.Entry(cv).Property(c => c.Version).OriginalValue = version;
        cv.State = CvState.Published;

        try
        {
            await _db.SaveChangesAsync();
            TempData["Status"] = "CV published - recruiters can see it now.";
        }
        catch (DbUpdateConcurrencyException)
        {
            TempData["Error"] = "This CV was changed elsewhere. Reload the page and try again.";
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unpublish(int id, int version)
    {
        var cv = await _db.Cvs.FirstOrDefaultAsync(c => c.Id == id);
        if (cv is null)
        {
            return NotFound();
        }

        if (cv.UserId != _userManager.GetUserId(User) && !User.IsInRole(Roles.Admin))
        {
            return Forbid();
        }

        _db.Entry(cv).Property(c => c.Version).OriginalValue = version;
        cv.State = CvState.Draft;

        try
        {
            await _db.SaveChangesAsync();
            TempData["Status"] = "CV moved back to draft.";
        }
        catch (DbUpdateConcurrencyException)
        {
            TempData["Error"] = "This CV was changed elsewhere. Reload the page and try again.";
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int[] ids)
    {
        var userId = _userManager.GetUserId(User)!;
        var isAdmin = User.IsInRole(Roles.Admin);

        var cvs = await _db.Cvs
            .Where(c => ids.Contains(c.Id) && (isAdmin || c.UserId == userId))
            .ToListAsync();

        _db.Cvs.RemoveRange(cvs);
        await _db.SaveChangesAsync();

        TempData["Status"] = $"Deleted {cvs.Count} CV(s).";
        return RedirectToAction("Index", "Profile");
    }

    // Only recruiters may like, at most once per CV. The unique index on
    // (CvId, UserId) is what actually enforces "at most one".
    [Authorize(Roles = Roles.RecruiterOrAdmin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleLike(int id)
    {
        var userId = _userManager.GetUserId(User)!;

        var like = await _db.CvLikes.FirstOrDefaultAsync(l => l.CvId == id && l.UserId == userId);

        if (like is null)
        {
            _db.CvLikes.Add(new CvLike { CvId = id, UserId = userId });
        }
        else
        {
            _db.CvLikes.Remove(like);
        }

        await _db.SaveChangesAsync();

        return RedirectToAction(nameof(Details), new { id });
    }
}
