using System.ComponentModel.DataAnnotations;

namespace CvManager.Models.ViewModels;

public class ProfileViewModel
{
    public required ApplicationUser Owner { get; init; }

    // "Me": the built-in attributes that always exist.
    public List<CvAttributeRow> MeAttributes { get; init; } = [];

    // "Info": the ones this candidate picked from the library.
    public List<CvAttributeRow> InfoAttributes { get; init; } = [];

    public List<Project> Projects { get; init; } = [];

    public List<ProfileCvItem> Cvs { get; init; } = [];

    public List<AttributeCategory> Categories { get; init; } = [];

    // True for the owner and for admins, who act as the owner everywhere.
    public bool CanEdit { get; init; }
}

public class ProfileCvItem
{
    public int Id { get; init; }
    public string PositionTitle { get; init; } = "";
    public CvState State { get; init; }
    public int Likes { get; init; }
    public DateTime UpdatedAt { get; init; }

    // The CV row stays in the database; it is only hidden in the UI when the
    // candidate no longer satisfies the position's access rules.
    public bool Hidden { get; init; }
}

public class ProjectEditViewModel
{
    public int Id { get; set; }

    [Required, StringLength(300)]
    [Display(Name = "Name")]
    public string Name { get; set; } = "";

    [Required]
    [Display(Name = "From")]
    public DateOnly StartDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);

    [Display(Name = "To (empty = ongoing)")]
    public DateOnly? EndDate { get; set; }

    [Display(Name = "Description (Markdown)")]
    public string? DescriptionMarkdown { get; set; }

    [Display(Name = "Technology tags")]
    public string? Tags { get; set; }

    public int Version { get; set; }

    // Set when an admin edits somebody else's project.
    public string? OwnerId { get; set; }
}
