using Microsoft.AspNetCore.Identity;

namespace CvManager.Models;

// Identity gives us Id, Email, UserName, password hash and the role tables.
// I only add what Identity has no place for: the saved UI preferences, the
// blocked flag and the navigation properties.
public class ApplicationUser : IdentityUser
{
    // "en" or "ru". Saved here so the choice survives a new browser/device;
    // a cookie alone would not.
    public string Language { get; set; } = "en";

    // "light" or "dark", same reason.
    public string Theme { get; set; } = "light";

    // Admins block users. I keep my own flag instead of Identity's LockoutEnd
    // because blocking here is permanent until an admin lifts it, and a bool is
    // much easier to filter on in queries than a nullable date.
    public bool IsBlocked { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // --- Salesforce CRM integration -------------------------------------
    //
    // The ids Salesforce gave back the last time this user was pushed to the
    // CRM. They are the whole reason a second sync updates the same two records
    // instead of creating a duplicate Account every time the button is pressed:
    // when these are filled the service sends PATCH, when they are empty it
    // sends POST. They live here rather than in their own table because
    // Language, Theme and IsBlocked already do, and one 1:1 table for three
    // strings would be a join for no benefit.
    //
    // Salesforce ids are 15 or 18 characters, so 18 is the real maximum; I use
    // a slightly wider column so nothing breaks if Salesforce ever changes it.
    public string? SalesforceAccountId { get; set; }

    public string? SalesforceContactId { get; set; }

    // When the last successful sync happened. Shown on the form so the user can
    // see whether they have ever pushed their details, and to which records.
    public DateTime? SalesforceSyncedAt { get; set; }

    // The extra details the sync form collects. The application itself has no
    // use for any of these - they exist so the form can pre-fill on the second
    // visit instead of making the user retype everything, and so the values
    // actually sent to the CRM are recoverable afterwards.
    public string? CompanyName { get; set; }

    public string? Industry { get; set; }

    public string? Website { get; set; }

    public string? Phone { get; set; }

    public string? JobTitle { get; set; }

    public string? City { get; set; }

    public string? Country { get; set; }

    // A user's answers to library attributes. This is the single master copy of
    // every value - profile pages and CVs both read and write these same rows,
    // which is why editing "English Level" in one CV changes it everywhere.
    public List<AttributeValue> AttributeValues { get; set; } = [];

    public List<Project> Projects { get; set; } = [];

    public List<Cv> Cvs { get; set; } = [];
}
