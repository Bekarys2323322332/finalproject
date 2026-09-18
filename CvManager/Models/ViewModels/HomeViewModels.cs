namespace CvManager.Models.ViewModels;

// A row of the positions table. This is a projection rather than the Position
// entity itself: the tables only need these columns, and selecting them keeps
// the query from pulling descriptions, rules and attributes it will not show.
public class PositionListItem
{
    public int Id { get; init; }
    public string Title { get; init; } = "";
    public string? Company { get; init; }
    public PositionLevel? Level { get; init; }
    public bool IsPublic { get; init; }
    public int CvCount { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public class TagCloudItem
{
    public string Name { get; init; } = "";
    public int Weight { get; init; }
}

public class HomeViewModel
{
    public List<PositionListItem> LatestPositions { get; init; } = [];
    public List<PositionListItem> PopularPositions { get; init; } = [];
    public List<TagCloudItem> Tags { get; init; } = [];

    public int CvsLast24Hours { get; init; }
    public int TotalPositions { get; init; }
    public int TotalCvs { get; init; }
    public int PublishedCvs { get; init; }
    public int TotalCandidates { get; init; }
    public int TotalRecruiters { get; init; }
}
