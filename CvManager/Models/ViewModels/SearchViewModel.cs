namespace CvManager.Models.ViewModels;

public class SearchViewModel
{
    public string? Query { get; set; }

    // Set from the tag cloud links, which send recruiters to CVs and candidates
    // to positions.
    public string? Scope { get; set; }

    public List<PositionListItem> Positions { get; set; } = [];
    public List<CvListItem> Cvs { get; set; } = [];
    public List<Project> MyProjects { get; set; } = [];

    public bool CanSeeCvs { get; set; }
}
