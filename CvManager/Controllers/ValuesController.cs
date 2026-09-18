using CvManager.Data;
using CvManager.Models;
using CvManager.Models.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CvManager.Controllers;

// The one endpoint that writes attribute values. Both the profile auto-save and
// the in-place editing inside a CV post here, which is what makes a value a
// single master copy: editing "English Level" in a CV and editing it on the
// profile are literally the same write.
//
// This is also where optimistic locking lives. Each item arrives with the
// version the page was rendered with; the update only applies if that version is
// still current, and a losing item comes back marked as a conflict together with
// the value that actually won.
[Authorize]
[Route("values")]
public class ValuesController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;

    public ValuesController(ApplicationDbContext db, UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _userManager = userManager;
    }

    [HttpPost("save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save([FromBody] SaveValuesRequest request)
    {
        var currentUserId = _userManager.GetUserId(User)!;
        var isAdmin = User.IsInRole(Roles.Admin);

        // Only an admin may write somebody else's values - they act as the owner
        // of every page. Everyone else silently gets their own id.
        var targetUserId = isAdmin && !string.IsNullOrWhiteSpace(request.UserId)
            ? request.UserId!
            : currentUserId;

        if (request.Items.Count == 0)
        {
            return Json(new SaveValuesResponse());
        }

        var attributeIds = request.Items.Select(i => i.AttributeId).Distinct().ToList();

        // Everything that already exists, in one query. The attribute type comes
        // along because it decides which column a value belongs in.
        var existing = await _db.AttributeValues
            .Where(v => v.UserId == targetUserId && attributeIds.Contains(v.AttributeId))
            .ToDictionaryAsync(v => v.AttributeId);

        var types = await _db.LibraryAttributes
            .Where(a => attributeIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Type);

        var touched = new List<AttributeValue>();

        foreach (var item in request.Items)
        {
            if (!types.TryGetValue(item.AttributeId, out var type))
            {
                continue;
            }

            if (existing.TryGetValue(item.AttributeId, out var row))
            {
                // The version the client held becomes the original value, so EF
                // writes "UPDATE ... WHERE Id = @id AND Version = @clientVersion".
                _db.Entry(row).Property(v => v.Version).OriginalValue = item.Version;
            }
            else
            {
                // First time this candidate answers this attribute. A brand new
                // row cannot conflict with anything.
                row = new AttributeValue { UserId = targetUserId, AttributeId = item.AttributeId };
                _db.AttributeValues.Add(row);
            }

            Apply(row, item, type);
            touched.Add(row);
        }

        var results = new List<ValueResult>();

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Somebody saved at least one of these rows first. EF hands back the
            // entries that lost, so the losers are dropped, reported as
            // conflicts, and the rest is saved in a second statement - the
            // candidate does not lose the other fields they just typed.
            var conflicted = ex.Entries.Select(e => (AttributeValue)e.Entity).ToList();

            foreach (var entry in ex.Entries)
            {
                entry.State = EntityState.Detached;
            }

            foreach (var row in conflicted)
            {
                touched.Remove(row);
            }

            await _db.SaveChangesAsync();

            // Read what actually won, so the client can display it instead of the
            // stale value the user was looking at.
            var conflictIds = conflicted.Select(c => c.AttributeId).ToList();
            var winners = await _db.AttributeValues
                .Where(v => v.UserId == targetUserId && conflictIds.Contains(v.AttributeId))
                .AsNoTracking()
                .ToListAsync();

            results.AddRange(winners.Select(w => ToResult(w, conflict: true)));
        }

        results.AddRange(touched.Select(t => ToResult(t, conflict: false)));

        return Json(new SaveValuesResponse { Results = results });
    }

    // Copies the incoming value into the column that matches the attribute's
    // type and clears the others, so a value can never be half numeric and half
    // text after a type change.
    private static void Apply(AttributeValue row, ValueDto item, AttributeType type)
    {
        row.ValueString = null;
        row.ValueNumber = null;
        row.ValueDate = null;
        row.ValueDateEnd = null;
        row.ValueBoolean = null;
        row.ValueOptionId = null;

        switch (type)
        {
            case AttributeType.Numeric:
                row.ValueNumber = item.ValueNumber;
                break;
            case AttributeType.Date:
                row.ValueDate = item.ValueDate;
                break;
            case AttributeType.Period:
                row.ValueDate = item.ValueDate;
                row.ValueDateEnd = item.ValueDateEnd;
                break;
            case AttributeType.Boolean:
                row.ValueBoolean = item.ValueBoolean ?? false;
                break;
            case AttributeType.Dropdown:
                row.ValueOptionId = item.ValueOptionId;
                break;
            default:
                row.ValueString = item.ValueString;
                break;
        }

        row.HasValue = type switch
        {
            // A checkbox always has an answer once it has been touched.
            AttributeType.Boolean => item.HasValue,
            AttributeType.Numeric => item.ValueNumber.HasValue,
            AttributeType.Date => item.ValueDate.HasValue,
            AttributeType.Period => item.ValueDate.HasValue,
            AttributeType.Dropdown => item.ValueOptionId.HasValue,
            _ => !string.IsNullOrWhiteSpace(item.ValueString)
        };
    }

    private static ValueResult ToResult(AttributeValue row, bool conflict) => new()
    {
        AttributeId = row.AttributeId,
        Version = row.Version,
        Conflict = conflict,
        ValueString = row.ValueString,
        ValueNumber = row.ValueNumber,
        ValueDate = row.ValueDate,
        ValueDateEnd = row.ValueDateEnd,
        ValueBoolean = row.ValueBoolean,
        ValueOptionId = row.ValueOptionId,
        HasValue = row.HasValue
    };
}
