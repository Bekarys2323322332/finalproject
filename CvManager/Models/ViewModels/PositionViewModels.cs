using System.ComponentModel.DataAnnotations;

namespace CvManager.Models.ViewModels;

// A CV as it appears in a list (position page, profile page, search results).
// Again a projection - the name is pulled out of the candidate's attribute
// values in the same query instead of loading each candidate separately.
public class CvListItem
{
    public int Id { get; init; }
    public int PositionId { get; init; }
    public string PositionTitle { get; init; } = "";
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Email { get; init; }
    public CvState State { get; init; }
    public int Likes { get; init; }
    public DateTime UpdatedAt { get; init; }

    public string CandidateName
    {
        get
        {
            var name = $"{FirstName} {LastName}".Trim();
            return name.Length > 0 ? name : Email ?? "";
        }
    }
}

public class PositionIndexViewModel
{
    public List<PositionListItem> Items { get; init; } = [];
    public string? Query { get; init; }
    public int Page { get; init; } = 1;
    public int TotalPages { get; init; } = 1;

    // Candidates see which positions they may actually build a CV for; the rest
    // are listed but not offered.
    public HashSet<int> AccessibleIds { get; init; } = [];
    public bool CanManage { get; init; }
}

// The basic-information form of the position editor. Kept separate from the
// attribute and rule editors so each posts only what it owns and carries its
// own version.
public class PositionBasicsViewModel
{
    public int Id { get; set; }

    [Required, StringLength(300)]
    [Display(Name = "Title")]
    public string Title { get; set; } = "";

    [StringLength(2000)]
    [Display(Name = "Short description")]
    public string? ShortDescription { get; set; }

    [StringLength(200)]
    [Display(Name = "Company")]
    public string? Company { get; set; }

    [Display(Name = "Level")]
    public PositionLevel? Level { get; set; }

    [Display(Name = "Public (any signed-in user may apply)")]
    public bool IsPublic { get; set; } = true;

    [Range(1, 20)]
    [Display(Name = "Maximum projects in the generated CV")]
    public int MaxProjects { get; set; } = 3;

    public int Version { get; set; }
}

// Everything the position editor shows at once.
public class PositionEditViewModel
{
    public required Position Position { get; init; }
    public required PositionBasicsViewModel Basics { get; init; }

    public List<PositionAttribute> Attributes { get; init; } = [];
    public List<PositionAccessRule> Rules { get; init; } = [];
    public List<string> ProjectTags { get; init; } = [];
    public List<AttributeCategory> Categories { get; init; } = [];
}

public class PositionDetailsViewModel
{
    public required Position Position { get; init; }
    public List<LibraryAttribute> Attributes { get; init; } = [];
    public List<PositionAccessRule> Rules { get; init; } = [];
    public List<string> ProjectTags { get; init; } = [];

    // Only filled for recruiters and admins.
    public List<CvListItem> Cvs { get; init; } = [];

    public bool CanManage { get; init; }
    public bool CanSeeCvs { get; init; }

    // For a candidate: may they build a CV here, and do they already have one?
    public bool CanApply { get; init; }
    public int? MyCvId { get; init; }
}

public class DiscussionPostViewModel
{
    public int Id { get; init; }
    public string AuthorName { get; init; } = "";
    public string? AuthorId { get; init; }
    public DateTime CreatedAt { get; init; }
    public string Html { get; init; } = "";
}
