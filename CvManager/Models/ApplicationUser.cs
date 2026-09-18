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

    // A user's answers to library attributes. This is the single master copy of
    // every value - profile pages and CVs both read and write these same rows,
    // which is why editing "English Level" in one CV changes it everywhere.
    public List<AttributeValue> AttributeValues { get; set; } = [];

    public List<Project> Projects { get; set; } = [];

    public List<Cv> Cvs { get; set; } = [];
}
