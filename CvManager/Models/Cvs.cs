namespace CvManager.Models;

// The CV is almost an empty record on purpose. It stores only the fact that this
// candidate made a CV for this position, plus its state - none of the content.
// Title, skills, projects are all looked up at render time from the candidate's
// AttributeValues and Projects, which is why a CV updates itself when the
// profile changes and why losing access only hides it instead of destroying it.
public class Cv : IVersioned
{
    public int Id { get; set; }

    public int PositionId { get; set; }
    public Position? Position { get; set; }

    public string UserId { get; set; } = "";
    public ApplicationUser? User { get; set; }

    // One CV per candidate per position - enforced by a unique index on
    // (PositionId, UserId), not by a lookup before insert.
    public CvState State { get; set; } = CvState.Draft;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public int Version { get; set; }

    public List<CvLike> Likes { get; set; } = [];
}

// One recruiter's like of one CV. The unique index on (CvId, UserId) is what
// stops a recruiter liking the same CV twice; removing a like deletes the row.
public class CvLike
{
    public int Id { get; set; }

    public int CvId { get; set; }
    public Cv? Cv { get; set; }

    public string UserId { get; set; } = "";
    public ApplicationUser? User { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// A message in a position's discussion. Append-only: there is no edit or delete
// action, and the list is always ordered by CreatedAt, so nothing can appear
// between two existing posts.
public class DiscussionPost
{
    public int Id { get; set; }

    public int PositionId { get; set; }
    public Position? Position { get; set; }

    public string AuthorId { get; set; } = "";
    public ApplicationUser? Author { get; set; }

    public string BodyMarkdown { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
