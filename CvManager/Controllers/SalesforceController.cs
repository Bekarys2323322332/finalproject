using CvManager.Data;
using CvManager.Models;
using CvManager.Models.ViewModels;
using CvManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CvManager.Controllers;

// Pushes a user's details into Salesforce as an Account with a linked Contact.
//
// This controller deliberately does very little: work out whose profile is being
// synced, check that the caller is allowed to touch it, build the data, hand it
// to SalesforceService and store whatever ids come back. Every line of HTTP,
// OAuth and JSON lives in the service.
[Authorize]
public class SalesforceController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly SalesforceService _salesforce;
    private readonly UserManager<ApplicationUser> _userManager;

    public SalesforceController(ApplicationDbContext db, SalesforceService salesforce,
        UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _salesforce = salesforce;
        _userManager = userManager;
    }

    // The form. ownerId is only honoured for admins - see ResolveTarget.
    [HttpGet]
    public async Task<IActionResult> Sync(string? ownerId)
    {
        var targetId = ResolveTarget(ownerId);
        if (targetId is null)
        {
            return Forbid();
        }

        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == targetId);
        if (user is null)
        {
            return NotFound();
        }

        return View(await BuildViewModelAsync(user, ownerId));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Sync(SalesforceSyncViewModel model)
    {
        var targetId = ResolveTarget(model.OwnerId);
        if (targetId is null)
        {
            return Forbid();
        }

        // Tracked, not AsNoTracking: the ids that come back are saved onto this
        // same row at the end of the method.
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == targetId);
        if (user is null)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            // Re-fill the parts of the page the user cannot edit before showing
            // the form again, otherwise the name and email would come back blank.
            return View(await BuildViewModelAsync(user, model.OwnerId, model));
        }

        // The three fields the application guarantees every user has. Names are
        // attribute values rather than columns in this application, so they are
        // read out of the attribute table by their well-known ids.
        var names = await ReadSystemNamesAsync(targetId);

        var data = new SalesforceSyncData
        {
            FirstName = names.FirstName,
            LastName = names.LastName,
            Email = user.Email ?? "",

            CompanyName = model.CompanyName,
            Industry = model.Industry,
            Website = model.Website,
            Phone = model.Phone,
            JobTitle = model.JobTitle,
            City = model.City,
            Country = model.Country,

            // When these are set the service updates those records instead of
            // creating new ones, which is what makes a second sync safe.
            ExistingAccountId = user.SalesforceAccountId,
            ExistingContactId = user.SalesforceContactId
        };

        var result = await _salesforce.SyncAsync(data);

        if (!result.Success)
        {
            // Shown on the form itself rather than as a page-level alert, so the
            // user keeps everything they typed and can correct it and retry.
            ModelState.AddModelError(string.Empty, result.ErrorMessage
                ?? "The Salesforce sync did not complete.");

            return View(await BuildViewModelAsync(user, model.OwnerId, model));
        }

        // Remember the record ids so the next sync updates rather than
        // duplicates, and remember the form values so the next visit pre-fills.
        user.SalesforceAccountId = result.AccountId;
        user.SalesforceContactId = result.ContactId;
        user.SalesforceSyncedAt = DateTime.UtcNow;

        user.CompanyName = model.CompanyName.Trim();
        user.Industry = model.Industry;
        user.Website = model.Website?.Trim();
        user.Phone = model.Phone?.Trim();
        user.JobTitle = model.JobTitle?.Trim();
        user.City = model.City?.Trim();
        user.Country = model.Country?.Trim();

        await _db.SaveChangesAsync();

        // The ids are named in the message on purpose: it means a demo can cut
        // from this alert straight to the record in Salesforce with the same id
        // on screen, which is far more convincing than a bare "Saved".
        var verb = result.Created ? "created" : "updated";
        TempData["Status"] =
            $"Salesforce sync complete. Account {result.AccountId} and Contact {result.ContactId} were {verb}.";

        return RedirectToAction(nameof(Sync), new { ownerId = model.OwnerId });
    }

    // Which user is being synced, or null when the caller may not do this.
    //
    // This is the whole of the authorisation rule the task asks for: the profile
    // owner in any role, or an admin. Hiding the button on the profile page is
    // cosmetic - this check is what actually enforces it, so posting the form
    // directly with somebody else's id gets a 403.
    private string? ResolveTarget(string? ownerId)
    {
        var me = _userManager.GetUserId(User);
        if (me is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(ownerId) || ownerId == me)
        {
            return me;
        }

        // A different user was asked for, so only an admin gets through.
        return User.IsInRole(Roles.Admin) ? ownerId : null;
    }

    // Reads First Name and Last Name out of the attribute table.
    //
    // These are the "non-removable" fields: they are system attributes, which
    // ProfileController refuses to delete, so every user is guaranteed to have
    // the rows even when they are still empty.
    private async Task<(string FirstName, string LastName, string? Location)> ReadSystemNamesAsync(string userId)
    {
        var ids = new[]
        {
            ApplicationDbContext.FirstNameAttributeId,
            ApplicationDbContext.LastNameAttributeId,
            ApplicationDbContext.LocationAttributeId
        };

        // One query for all three rather than one lookup per field.
        var values = await _db.AttributeValues
            .Where(v => v.UserId == userId && ids.Contains(v.AttributeId) && v.HasValue)
            .Select(v => new { v.AttributeId, v.ValueString })
            .AsNoTracking()
            .ToDictionaryAsync(v => v.AttributeId, v => v.ValueString);

        return (
            values.GetValueOrDefault(ApplicationDbContext.FirstNameAttributeId) ?? "",
            values.GetValueOrDefault(ApplicationDbContext.LastNameAttributeId) ?? "",
            values.GetValueOrDefault(ApplicationDbContext.LocationAttributeId)
        );
    }

    // Builds the page model. When posted is supplied its values win, so a failed
    // submission redisplays what the user typed instead of the saved values.
    private async Task<SalesforceSyncViewModel> BuildViewModelAsync(ApplicationUser user, string? ownerId,
        SalesforceSyncViewModel? posted = null)
    {
        var names = await ReadSystemNamesAsync(user.Id);

        var displayName = string.Join(" ", new[] { names.FirstName, names.LastName }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

        var model = posted ?? new SalesforceSyncViewModel
        {
            // First visit: pre-fill from whatever the last sync used, and seed
            // the city from the Location attribute so the user usually only has
            // to confirm it. Location is one free-text line ("Krakow, Poland"),
            // so only the part before the comma can safely be treated as a city.
            CompanyName = user.CompanyName ?? "",
            Industry = user.Industry,
            Website = user.Website,
            Phone = user.Phone,
            JobTitle = user.JobTitle,
            City = user.City ?? SplitLocation(names.Location).City,
            Country = user.Country ?? SplitLocation(names.Location).Country
        };

        model.OwnerId = ownerId;
        model.DisplayName = string.IsNullOrWhiteSpace(displayName) ? user.Email ?? "" : displayName;
        model.Email = user.Email ?? "";
        model.LocationHint = names.Location;
        model.SalesforceAccountId = user.SalesforceAccountId;
        model.SalesforceContactId = user.SalesforceContactId;
        model.LastSyncedAt = user.SalesforceSyncedAt;
        model.IsConfigured = _salesforce.IsConfigured;
        model.OrgBaseUrl = _salesforce.OrgBaseUrl;

        return model;
    }

    // Location is a single free-text attribute, so this is a best guess and
    // nothing more: "Krakow, Poland" splits cleanly, "Remote" does not. Whatever
    // it produces is only a pre-fill the user can correct before submitting.
    private static (string? City, string? Country) SplitLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return (null, null);
        }

        var parts = location.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parts.Length >= 2
            ? (parts[0], parts[^1])
            : (parts[0], null);
    }
}
