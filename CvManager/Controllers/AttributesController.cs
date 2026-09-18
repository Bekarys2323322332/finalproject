using CvManager.Data;
using CvManager.Models;
using CvManager.Models.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CvManager.Controllers;

// Killer feature #1: the shared attribute library.
// Recruiters and admins manage it; there is no owner, any of them may edit or
// delete anything, which is why no action checks who created a row.
//
// The class only requires a signed-in user because the two lookup actions at the
// bottom (Suggest and Options) feed the attribute picker on the candidate's own
// profile as well. Every managing action carries the role itself.
[Authorize]
public class AttributesController : Controller
{
    private const int PageSize = 20;

    private readonly ApplicationDbContext _db;

    public AttributesController(ApplicationDbContext db)
    {
        _db = db;
    }

    [Authorize(Roles = Roles.RecruiterOrAdmin)]
    public async Task<IActionResult> Index(string? q, int? categoryId, int page = 1)
    {
        var query = _db.LibraryAttributes.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(q))
        {
            // Prefix lookup, so the Name index can be used instead of scanning
            // every row with a leading wildcard.
            query = query.Where(a => EF.Functions.ILike(a.Name, q + "%"));
        }

        if (categoryId.HasValue)
        {
            query = query.Where(a => a.CategoryId == categoryId.Value);
        }

        var total = await query.CountAsync();
        page = Math.Max(1, page);

        var items = await query
            .OrderBy(a => a.Name)
            .Skip((page - 1) * PageSize)
            .Take(PageSize)
            .Include(a => a.Category)
            .ToListAsync();

        return View(new AttributeIndexViewModel
        {
            Items = items,
            Categories = await CategoriesAsync(),
            Query = q,
            CategoryId = categoryId,
            Page = page,
            TotalPages = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize))
        });
    }

    [HttpGet]
    [Authorize(Roles = Roles.RecruiterOrAdmin)]
    public async Task<IActionResult> Create()
    {
        return View("Edit", new AttributeEditViewModel { Categories = await CategoriesAsync() });
    }

    [HttpGet]
    [Authorize(Roles = Roles.RecruiterOrAdmin)]
    public async Task<IActionResult> Edit(int id)
    {
        var attribute = await _db.LibraryAttributes
            .Include(a => a.Options.OrderBy(o => o.SortOrder))
            .FirstOrDefaultAsync(a => a.Id == id);

        if (attribute is null)
        {
            return NotFound();
        }

        return View(new AttributeEditViewModel
        {
            Id = attribute.Id,
            Name = attribute.Name,
            CategoryId = attribute.CategoryId,
            Description = attribute.Description,
            Type = attribute.Type,
            IsSystem = attribute.IsSystem,
            Version = attribute.Version,
            OptionsText = string.Join("\n", attribute.Options.Select(o => o.Value)),
            Categories = await CategoriesAsync()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = Roles.RecruiterOrAdmin)]
    public async Task<IActionResult> Save(AttributeEditViewModel model)
    {
        if (!ModelState.IsValid)
        {
            model.Categories = await CategoriesAsync();
            return View("Edit", model);
        }

        var isNew = model.Id == 0;

        LibraryAttribute attribute;
        if (isNew)
        {
            attribute = new LibraryAttribute();
            _db.LibraryAttributes.Add(attribute);
        }
        else
        {
            var existing = await _db.LibraryAttributes
                .Include(a => a.Options)
                .FirstOrDefaultAsync(a => a.Id == model.Id);

            if (existing is null)
            {
                return NotFound();
            }

            attribute = existing;

            // Optimistic locking. The version the form was rendered with is put
            // back as the original value, so EF writes
            // "UPDATE ... WHERE Id = @id AND Version = @formVersion" and the save
            // fails if anybody else saved in between.
            _db.Entry(attribute).Property(a => a.Version).OriginalValue = model.Version;
        }

        attribute.Name = model.Name.Trim();
        attribute.CategoryId = model.CategoryId;
        attribute.Description = model.Description;

        // A built-in attribute keeps its type: profile pages and the CV header
        // read its value from a specific column, so letting a recruiter turn
        // "First Name" into a checkbox would break them.
        if (!attribute.IsSystem)
        {
            attribute.Type = model.Type;
        }

        if (attribute.Type == AttributeType.Dropdown)
        {
            SyncOptions(attribute, model.OptionsText);
        }
        else
        {
            attribute.Options.Clear();
        }

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Somebody saved this attribute while the form was open. Reload and
            // let the user decide, rather than silently overwriting their work.
            ModelState.AddModelError(string.Empty,
                "This attribute was changed by somebody else while you were editing it. Reload the page and apply your changes again.");
            model.Categories = await CategoriesAsync();
            return View("Edit", model);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // The unique index on Name rejected it. Catching the database error
            // is what makes the name genuinely unique - a check before the insert
            // would still let two simultaneous saves through.
            ModelState.AddModelError(nameof(model.Name), "An attribute with this name already exists.");
            model.Categories = await CategoriesAsync();
            return View("Edit", model);
        }

        TempData["Status"] = isNew ? "Attribute created." : "Attribute saved.";
        return RedirectToAction(nameof(Index));
    }

    // The toolbar's Edit button posts the checked row here rather than linking
    // per row, because links in rows are what the spec forbids.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = Roles.RecruiterOrAdmin)]
    public IActionResult EditSelected(int[] ids)
    {
        return ids.Length == 1
            ? RedirectToAction(nameof(Edit), new { id = ids[0] })
            : RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = Roles.RecruiterOrAdmin)]
    public async Task<IActionResult> Delete(int[] ids)
    {
        if (ids.Length == 0)
        {
            return RedirectToAction(nameof(Index));
        }

        // Built-in attributes are filtered out here, not in the UI only, so a
        // crafted request cannot remove them either.
        var deletable = await _db.LibraryAttributes
            .Where(a => ids.Contains(a.Id) && !a.IsSystem)
            .ToListAsync();

        var skipped = ids.Length - deletable.Count;

        _db.LibraryAttributes.RemoveRange(deletable);

        // One DELETE. Every dependent row - the values candidates filled in, the
        // position templates using it, the access rules - goes with it through
        // the cascade rules declared in the DbContext, so there is no loop here
        // deleting children by hand.
        await _db.SaveChangesAsync();

        TempData["Status"] = skipped > 0
            ? $"Deleted {deletable.Count} attribute(s). {skipped} built-in attribute(s) cannot be deleted."
            : $"Deleted {deletable.Count} attribute(s).";

        return RedirectToAction(nameof(Index));
    }

    // Feeds the attribute picker used by the profile page and the position
    // editor: prefix lookup, category filter, and recently used first when the
    // box is still empty.
    [HttpGet]
    public async Task<IActionResult> Suggest(string? q, int? categoryId, int take = 15)
    {
        var query = _db.LibraryAttributes.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(a => EF.Functions.ILike(a.Name, q + "%"));
        }

        if (categoryId.HasValue)
        {
            query = query.Where(a => a.CategoryId == categoryId.Value);
        }

        query = string.IsNullOrWhiteSpace(q)
            ? query.OrderByDescending(a => a.LastUsedAt).ThenBy(a => a.Name)
            : query.OrderBy(a => a.Name);

        var items = await query
            .Take(Math.Clamp(take, 1, 50))
            .Select(a => new
            {
                id = a.Id,
                name = a.Name,
                type = a.Type.ToString(),
                category = a.Category!.Name,
                description = a.Description
            })
            .ToListAsync();

        return Json(items);
    }

    // The rule editor needs the choices of a dropdown attribute, because a rule
    // on a dropdown stores the option's id rather than its text.
    [HttpGet]
    public async Task<IActionResult> Options(int attributeId)
    {
        var options = await _db.AttributeOptions
            .Where(o => o.AttributeId == attributeId)
            .OrderBy(o => o.SortOrder)
            .Select(o => new { id = o.Id, value = o.Value })
            .ToListAsync();

        return Json(options);
    }

    private Task<List<AttributeCategory>> CategoriesAsync()
    {
        return _db.AttributeCategories.AsNoTracking().OrderBy(c => c.Name).ToListAsync();
    }

    // Replaces the option list from the textarea. Options that keep their text
    // keep their row - and therefore their id - so candidates who already chose
    // them do not lose their answer.
    private static void SyncOptions(LibraryAttribute attribute, string? optionsText)
    {
        var wanted = (optionsText ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct()
            .ToList();

        attribute.Options.RemoveAll(o => !wanted.Contains(o.Value));

        for (var i = 0; i < wanted.Count; i++)
        {
            var option = attribute.Options.FirstOrDefault(o => o.Value == wanted[i]);
            if (option is null)
            {
                attribute.Options.Add(new AttributeOption { Value = wanted[i], SortOrder = i });
            }
            else
            {
                option.SortOrder = i;
            }
        }
    }

    // Postgres reports a unique constraint violation as SQLSTATE 23505.
    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        return ex.InnerException is PostgresException { SqlState: "23505" };
    }
}
