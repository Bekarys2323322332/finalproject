using System.Globalization;
using CvManager.Data;
using CvManager.Models;
using CvManager.Models.ViewModels;
using CvManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CvManager.Controllers;

// Killer feature #2: positions, which double as CV templates.
// The list and the details page are readable by anyone (visitors included);
// everything that changes a position requires the Recruiter or Admin role.
public class PositionsController : Controller
{
    private const int PageSize = 20;

    // How long a user must wait between two posts in the same discussion.
    private static readonly TimeSpan PostCooldown = TimeSpan.FromMinutes(1);

    private readonly ApplicationDbContext _db;
    private readonly PositionAccessService _access;
    private readonly TagService _tags;
    private readonly MarkdownRenderer _markdown;
    private readonly CvCsvExporter _csv;
    private readonly UserManager<ApplicationUser> _userManager;

    public PositionsController(ApplicationDbContext db, PositionAccessService access,
        TagService tags, MarkdownRenderer markdown, CvCsvExporter csv,
        UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _access = access;
        _tags = tags;
        _markdown = markdown;
        _csv = csv;
        _userManager = userManager;
    }

    private bool CanManage => User.IsInRole(Roles.Recruiter) || User.IsInRole(Roles.Admin);

    [AllowAnonymous]
    public async Task<IActionResult> Index(string? q, int page = 1)
    {
        var query = _db.Positions.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(q))
        {
            // Full-text match on the generated tsvector column. websearch_to_tsquery
            // accepts what a user would actually type, quotes and OR included.
            query = query.Where(p => p.SearchVector!.Matches(EF.Functions.WebSearchToTsQuery("english", q)));
        }

        var total = await query.CountAsync();
        page = Math.Max(1, page);

        var items = await query
            .OrderByDescending(p => p.UpdatedAt)
            .Skip((page - 1) * PageSize)
            .Take(PageSize)
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

        // For a candidate, mark which of the listed positions they may apply for.
        // One extra query for the visible page only, not one per row.
        var accessible = new HashSet<int>();
        var userId = _userManager.GetUserId(User);
        if (userId is not null && !CanManage)
        {
            var ids = items.Select(i => i.Id).ToList();
            accessible = (await _access.AccessibleTo(userId)
                    .Where(p => ids.Contains(p.Id))
                    .Select(p => p.Id)
                    .ToListAsync())
                .ToHashSet();
        }

        return View(new PositionIndexViewModel
        {
            Items = items,
            Query = q,
            Page = page,
            TotalPages = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize)),
            AccessibleIds = accessible,
            CanManage = CanManage
        });
    }

    [AllowAnonymous]
    public async Task<IActionResult> Details(int id)
    {
        var position = await _db.Positions
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id);

        if (position is null)
        {
            return NotFound();
        }

        // Include has to come before the projection - EF cannot apply it to an
        // already projected entity - so the join is loaded and then reshaped in
        // memory. It is still a single query.
        var attributeRows = await _db.PositionAttributes
            .Where(pa => pa.PositionId == id)
            .OrderBy(pa => pa.SortOrder)
            .Include(pa => pa.Attribute!)
            .ThenInclude(a => a.Category)
            .AsNoTracking()
            .ToListAsync();

        var attributes = attributeRows.Select(pa => pa.Attribute!).ToList();

        var rules = await _db.PositionAccessRules
            .Where(r => r.PositionId == id)
            .Include(r => r.Attribute)
            .AsNoTracking()
            .ToListAsync();

        var tags = await _db.PositionProjectTags
            .Where(pt => pt.PositionId == id)
            .Select(pt => pt.Tag!.Name)
            .ToListAsync();

        var userId = _userManager.GetUserId(User);
        var isAdmin = User.IsInRole(Roles.Admin);
        var canSeeCvs = CanManage;

        var cvs = new List<CvListItem>();
        if (canSeeCvs)
        {
            // Recruiters only see published CVs; an admin sees drafts too, since
            // admins act as the owner of every page.
            var cvQuery = _db.Cvs.Where(c => c.PositionId == id);
            if (!isAdmin)
            {
                cvQuery = cvQuery.Where(c => c.State == CvState.Published);
            }

            cvs = await ProjectCvs(cvQuery).ToListAsync();
        }

        int? myCvId = null;
        var canApply = false;
        if (userId is not null)
        {
            myCvId = await _db.Cvs
                .Where(c => c.PositionId == id && c.UserId == userId)
                .Select(c => (int?)c.Id)
                .FirstOrDefaultAsync();

            canApply = await _access.CanAccessAsync(id, userId);
        }

        return View(new PositionDetailsViewModel
        {
            Position = position,
            Attributes = attributes,
            Rules = rules,
            ProjectTags = tags,
            Cvs = cvs,
            CanManage = CanManage,
            CanSeeCvs = canSeeCvs,
            CanApply = canApply,
            MyCvId = myCvId
        });
    }

    // Shared projection for CV lists, so the position page, the profile page and
    // the search results all build the rows the same way and in one query.
    private IQueryable<CvListItem> ProjectCvs(IQueryable<Cv> source)
    {
        return source
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new CvListItem
            {
                Id = c.Id,
                PositionId = c.PositionId,
                PositionTitle = c.Position!.Title,
                Email = c.User!.Email,
                State = c.State,
                Likes = c.Likes.Count,
                UpdatedAt = c.UpdatedAt,
                // Correlated sub-selects: the name lives in the attribute values
                // like any other answer, so it is read from there.
                FirstName = _db.AttributeValues
                    .Where(v => v.UserId == c.UserId && v.AttributeId == ApplicationDbContext.FirstNameAttributeId)
                    .Select(v => v.ValueString)
                    .FirstOrDefault(),
                LastName = _db.AttributeValues
                    .Where(v => v.UserId == c.UserId && v.AttributeId == ApplicationDbContext.LastNameAttributeId)
                    .Select(v => v.ValueString)
                    .FirstOrDefault()
            });
    }

    // Optional extra: every CV for this position as one spreadsheet, with a
    // column per attribute the position asks for.
    [Authorize(Roles = Roles.RecruiterOrAdmin)]
    public async Task<IActionResult> ExportCsv(int id)
    {
        var position = await _db.Positions.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (position is null)
        {
            return NotFound();
        }

        // Only an admin sees drafts, exactly as on the page itself.
        var bytes = await _csv.ExportAsync(id, includeDrafts: User.IsInRole(Roles.Admin));
        var fileName = $"cvs-{position.Title}.csv".Replace(' ', '-').Replace('/', '-');

        return File(bytes, "text/csv", fileName);
    }

    // Creating a position is two steps on purpose. The editor needs an id to
    // hang attributes and rules off, but inserting a blank row first meant a
    // mis-click left an empty position in the list forever. Now the row is only
    // written once the required fields are valid.
    [Authorize(Roles = Roles.RecruiterOrAdmin)]
    [HttpGet]
    public IActionResult Create()
    {
        return View(new PositionBasicsViewModel());
    }

    [Authorize(Roles = Roles.RecruiterOrAdmin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(PositionBasicsViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var position = new Position
        {
            Title = model.Title.Trim(),
            ShortDescription = model.ShortDescription,
            Company = model.Company,
            Level = model.Level,
            IsPublic = model.IsPublic,
            MaxProjects = model.MaxProjects
        };

        _db.Positions.Add(position);
        await _db.SaveChangesAsync();

        TempData["Status"] = "Position created. Add the attributes it should ask for.";
        return RedirectToAction(nameof(Edit), new { id = position.Id });
    }

    // Same trick as the attribute library: the toolbar posts the checked row and
    // this bounces to the editor, so the rows stay free of buttons and links.
    [Authorize(Roles = Roles.RecruiterOrAdmin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult EditSelected(int[] ids)
    {
        return ids.Length == 1
            ? RedirectToAction(nameof(Edit), new { id = ids[0] })
            : RedirectToAction(nameof(Index));
    }

    [Authorize(Roles = Roles.RecruiterOrAdmin)]
    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var model = await BuildEditViewModelAsync(id);
        return model is null ? NotFound() : View(model);
    }

    // Shared by the editor and by SaveBasics when validation fails, so a failed
    // save can redisplay the page with its error messages instead of redirecting
    // and losing them.
    private async Task<PositionEditViewModel?> BuildEditViewModelAsync(
        int id, PositionBasicsViewModel? basics = null)
    {
        var position = await _db.Positions.FirstOrDefaultAsync(p => p.Id == id);
        if (position is null)
        {
            return null;
        }

        return new PositionEditViewModel
        {
            Position = position,
            // On a failed save the user's own input is shown back to them, not
            // the values still sitting in the database.
            Basics = basics ?? new PositionBasicsViewModel
            {
                Id = position.Id,
                Title = position.Title,
                ShortDescription = position.ShortDescription,
                Company = position.Company,
                Level = position.Level,
                IsPublic = position.IsPublic,
                MaxProjects = position.MaxProjects,
                Version = position.Version
            },
            Attributes = await _db.PositionAttributes
                .Where(pa => pa.PositionId == id)
                .OrderBy(pa => pa.SortOrder)
                .Include(pa => pa.Attribute)
                .ToListAsync(),
            Rules = await _db.PositionAccessRules
                .Where(r => r.PositionId == id)
                .Include(r => r.Attribute)
                .ToListAsync(),
            ProjectTags = await _db.PositionProjectTags
                .Where(pt => pt.PositionId == id)
                .Select(pt => pt.Tag!.Name)
                .ToListAsync(),
            Categories = await _db.AttributeCategories.OrderBy(c => c.Name).ToListAsync()
        };
    }

    [Authorize(Roles = Roles.RecruiterOrAdmin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    // The form lives inside PositionEditViewModel, so its fields are posted as
    // "Basics.Title", "Basics.Version" and so on. Without the prefix the binder
    // would look for "Title" at the top level, find nothing, and quietly save an
    // empty model.
    public async Task<IActionResult> SaveBasics([Bind(Prefix = "Basics")] PositionBasicsViewModel model)
    {
        var position = await _db.Positions.FirstOrDefaultAsync(p => p.Id == model.Id);
        if (position is null)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            // Redirecting here would throw the validation messages away, so the
            // editor is rebuilt around the values the user just submitted.
            var invalid = await BuildEditViewModelAsync(model.Id, model);
            return invalid is null ? NotFound() : View("Edit", invalid);
        }

        // Optimistic locking: the version the form was rendered with becomes the
        // original value, so a concurrent save by another recruiter is detected.
        _db.Entry(position).Property(p => p.Version).OriginalValue = model.Version;

        position.Title = model.Title.Trim();
        position.ShortDescription = model.ShortDescription;
        position.Company = model.Company;
        position.Level = model.Level;
        position.IsPublic = model.IsPublic;
        position.MaxProjects = model.MaxProjects;

        try
        {
            await _db.SaveChangesAsync();
            TempData["Status"] = "Position saved.";
        }
        catch (DbUpdateConcurrencyException)
        {
            TempData["Error"] = "Another recruiter saved this position while you were editing it. Your changes were not applied - review the reloaded values and try again.";
        }

        return RedirectToAction(nameof(Edit), new { id = model.Id });
    }

    [Authorize(Roles = Roles.RecruiterOrAdmin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddAttribute(int positionId, int attributeId)
    {
        var alreadyThere = await _db.PositionAttributes
            .AnyAsync(pa => pa.PositionId == positionId && pa.AttributeId == attributeId);

        if (!alreadyThere)
        {
            var nextOrder = await _db.PositionAttributes
                .Where(pa => pa.PositionId == positionId)
                .Select(pa => (int?)pa.SortOrder)
                .MaxAsync() ?? 0;

            _db.PositionAttributes.Add(new PositionAttribute
            {
                PositionId = positionId,
                AttributeId = attributeId,
                SortOrder = nextOrder + 1
            });

            await TouchAttributeAsync(attributeId);
            await _db.SaveChangesAsync();
        }

        return RedirectToAction(nameof(Edit), new { id = positionId });
    }

    [Authorize(Roles = Roles.RecruiterOrAdmin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveAttributes(int positionId, int[] attributeIds)
    {
        var rows = await _db.PositionAttributes
            .Where(pa => pa.PositionId == positionId && attributeIds.Contains(pa.AttributeId))
            .ToListAsync();

        _db.PositionAttributes.RemoveRange(rows);
        await _db.SaveChangesAsync();

        return RedirectToAction(nameof(Edit), new { id = positionId });
    }

    [Authorize(Roles = Roles.RecruiterOrAdmin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddRule(int positionId, int attributeId, RuleOperator op, string? value)
    {
        var attribute = await _db.LibraryAttributes
            .Include(a => a.Options)
            .FirstOrDefaultAsync(a => a.Id == attributeId);

        if (attribute is null)
        {
            return NotFound();
        }

        // The operator must be one the type actually supports, otherwise the rule
        // could never be satisfied and the position would silently be closed to
        // everybody.
        if (!PositionAccessService.OperatorsFor(attribute.Type).Contains(op))
        {
            TempData["Error"] = $"Operator {op} cannot be used with a {attribute.Type} attribute.";
            return RedirectToAction(nameof(Edit), new { id = positionId });
        }

        var rule = new PositionAccessRule
        {
            PositionId = positionId,
            AttributeId = attributeId,
            Operator = op
        };

        // The comparison value goes into the column matching the attribute's
        // type, which is what lets the access check compare like with like.
        if (op is not (RuleOperator.IsChecked or RuleOperator.IsNotChecked or RuleOperator.IsFilled))
        {
            switch (attribute.Type)
            {
                case AttributeType.Numeric:
                    if (!decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
                    {
                        TempData["Error"] = "Enter a number for this rule.";
                        return RedirectToAction(nameof(Edit), new { id = positionId });
                    }
                    rule.ValueNumber = number;
                    break;

                case AttributeType.Date:
                    if (!DateOnly.TryParse(value, CultureInfo.InvariantCulture, out var date))
                    {
                        TempData["Error"] = "Enter a date for this rule.";
                        return RedirectToAction(nameof(Edit), new { id = positionId });
                    }
                    rule.ValueDate = date;
                    break;

                case AttributeType.Dropdown:
                    var option = attribute.Options.FirstOrDefault(o => o.Id.ToString() == value);
                    if (option is null)
                    {
                        TempData["Error"] = "Choose one of the dropdown options for this rule.";
                        return RedirectToAction(nameof(Edit), new { id = positionId });
                    }
                    rule.ValueOptionId = option.Id;
                    break;

                default:
                    rule.ValueString = value;
                    break;
            }
        }

        _db.PositionAccessRules.Add(rule);
        await _db.SaveChangesAsync();

        return RedirectToAction(nameof(Edit), new { id = positionId });
    }

    [Authorize(Roles = Roles.RecruiterOrAdmin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveRules(int positionId, int[] ruleIds)
    {
        var rules = await _db.PositionAccessRules
            .Where(r => r.PositionId == positionId && ruleIds.Contains(r.Id))
            .ToListAsync();

        _db.PositionAccessRules.RemoveRange(rules);
        await _db.SaveChangesAsync();

        return RedirectToAction(nameof(Edit), new { id = positionId });
    }

    [Authorize(Roles = Roles.RecruiterOrAdmin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveProjectTags(int positionId, string? tags)
    {
        var names = (tags ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var resolved = await _tags.GetOrCreateAsync(names);

        var current = await _db.PositionProjectTags
            .Where(pt => pt.PositionId == positionId)
            .ToListAsync();

        _db.PositionProjectTags.RemoveRange(current);
        _db.PositionProjectTags.AddRange(resolved.Select(t => new PositionProjectTag
        {
            PositionId = positionId,
            TagId = t.Id
        }));

        await _db.SaveChangesAsync();

        return RedirectToAction(nameof(Edit), new { id = positionId });
    }

    // "Duplicate existing positions." Everything that defines the template is
    // copied; the CVs and the discussion are not, because they belong to the
    // original.
    [Authorize(Roles = Roles.RecruiterOrAdmin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Duplicate(int[] ids)
    {
        if (ids.Length != 1)
        {
            return RedirectToAction(nameof(Index));
        }

        var source = await _db.Positions
            .Include(p => p.PositionAttributes)
            .Include(p => p.AccessRules)
            .Include(p => p.ProjectTags)
            .FirstOrDefaultAsync(p => p.Id == ids[0]);

        if (source is null)
        {
            return NotFound();
        }

        var copy = new Position
        {
            Title = source.Title + " (copy)",
            ShortDescription = source.ShortDescription,
            Company = source.Company,
            Level = source.Level,
            IsPublic = source.IsPublic,
            MaxProjects = source.MaxProjects,
            PositionAttributes = source.PositionAttributes
                .Select(pa => new PositionAttribute { AttributeId = pa.AttributeId, SortOrder = pa.SortOrder })
                .ToList(),
            AccessRules = source.AccessRules
                .Select(r => new PositionAccessRule
                {
                    AttributeId = r.AttributeId,
                    Operator = r.Operator,
                    ValueString = r.ValueString,
                    ValueNumber = r.ValueNumber,
                    ValueDate = r.ValueDate,
                    ValueBoolean = r.ValueBoolean,
                    ValueOptionId = r.ValueOptionId
                })
                .ToList(),
            ProjectTags = source.ProjectTags
                .Select(pt => new PositionProjectTag { TagId = pt.TagId })
                .ToList()
        };

        _db.Positions.Add(copy);
        await _db.SaveChangesAsync();

        TempData["Status"] = "Position duplicated.";
        return RedirectToAction(nameof(Edit), new { id = copy.Id });
    }

    [Authorize(Roles = Roles.RecruiterOrAdmin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int[] ids)
    {
        var positions = await _db.Positions.Where(p => ids.Contains(p.Id)).ToListAsync();
        _db.Positions.RemoveRange(positions);

        // The CVs, discussion posts, likes, rules and template rows all go with
        // it through the cascade rules - the database does the work.
        await _db.SaveChangesAsync();

        TempData["Status"] = $"Deleted {positions.Count} position(s).";
        return RedirectToAction(nameof(Index));
    }

    // --- Discussion -------------------------------------------------------

    // Posts are appended only and returned in id order, so a new message can
    // never appear between two old ones. The page polls this every few seconds,
    // passing the last id it already has.
    [Authorize]
    [HttpGet]
    public async Task<IActionResult> Messages(int id, int afterId = 0)
    {
        var showProfileLinks = CanManage;

        var posts = await _db.DiscussionPosts
            .Where(p => p.PositionId == id && p.Id > afterId)
            .OrderBy(p => p.Id)
            .Select(p => new
            {
                p.Id,
                p.CreatedAt,
                p.BodyMarkdown,
                p.AuthorId,
                AuthorEmail = p.Author!.Email,
                FirstName = _db.AttributeValues
                    .Where(v => v.UserId == p.AuthorId && v.AttributeId == ApplicationDbContext.FirstNameAttributeId)
                    .Select(v => v.ValueString).FirstOrDefault(),
                LastName = _db.AttributeValues
                    .Where(v => v.UserId == p.AuthorId && v.AttributeId == ApplicationDbContext.LastNameAttributeId)
                    .Select(v => v.ValueString).FirstOrDefault()
            })
            .ToListAsync();

        var result = posts.Select(p => new DiscussionPostViewModel
        {
            Id = p.Id,
            AuthorName = $"{p.FirstName} {p.LastName}".Trim() is { Length: > 0 } name ? name : p.AuthorEmail ?? "",
            // Recruiters get a link to the author's profile, other viewers do not.
            AuthorId = showProfileLinks ? p.AuthorId : null,
            CreatedAt = p.CreatedAt,
            Html = _markdown.ToHtml(p.BodyMarkdown)
        });

        return Json(result);
    }

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    // One message per minute per person per discussion.
    //
    // The button is disabled in the browser while a post is in flight and for a
    // minute afterwards, but that only stops honest double-clicks: a second tab,
    // a refresh or a crafted request would still get through. The rule is
    // therefore enforced here, where it cannot be skipped.
    public async Task<IActionResult> PostMessage(int id, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return BadRequest("Write something first.");
        }

        var userId = _userManager.GetUserId(User)!;
        var cutoff = DateTime.UtcNow - PostCooldown;

        // Rides the (PositionId, Id) index; only the timestamp is fetched.
        var lastPostedAt = await _db.DiscussionPosts
            .Where(p => p.PositionId == id && p.AuthorId == userId)
            .OrderByDescending(p => p.Id)
            .Select(p => (DateTime?)p.CreatedAt)
            .FirstOrDefaultAsync();

        if (lastPostedAt is not null && lastPostedAt > cutoff)
        {
            var wait = (int)Math.Ceiling((lastPostedAt.Value - cutoff).TotalSeconds);

            // 429 rather than 400: the request was fine, it just came too soon.
            return StatusCode(StatusCodes.Status429TooManyRequests,
                $"Please wait {wait} more second(s) before posting again.");
        }

        _db.DiscussionPosts.Add(new DiscussionPost
        {
            PositionId = id,
            AuthorId = userId,
            BodyMarkdown = body.Trim()
        });

        await _db.SaveChangesAsync();

        return Ok();
    }

    // Marks an attribute as recently used so the picker can offer it first.
    private async Task TouchAttributeAsync(int attributeId)
    {
        var attribute = await _db.LibraryAttributes.FirstOrDefaultAsync(a => a.Id == attributeId);
        if (attribute is not null)
        {
            attribute.LastUsedAt = DateTime.UtcNow;
        }
    }
}
