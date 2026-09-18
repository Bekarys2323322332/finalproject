using NpgsqlTypes;

namespace CvManager.Models;

// A position is the CV template. All recruiters share every position - there is
// no owner column, any recruiter may edit or delete any of them.
public class Position : IVersioned
{
    public int Id { get; set; }

    public string Title { get; set; } = "";

    public string? ShortDescription { get; set; }

    // Optional extras, only there to make the positions table worth filtering
    // and sorting.
    public string? Company { get; set; }
    public PositionLevel? Level { get; set; }

    // Public means every authenticated user may create a CV for it. When false,
    // access is decided by AccessRules below.
    public bool IsPublic { get; set; } = true;

    // How many of the candidate's matching projects go into the generated CV.
    // The most recent ones win.
    public int MaxProjects { get; set; } = 3;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public int Version { get; set; }

    // Postgres builds this column itself from Title/ShortDescription/Company
    // (see the HasGeneratedTsVectorColumn call in ApplicationDbContext) and it
    // has a GIN index, so the header search is an index lookup instead of a
    // LIKE '%...%' scan over every row. I never assign to it.
    public NpgsqlTsVector? SearchVector { get; set; }

    // Which library attributes this position asks for, i.e. the sections of the
    // generated CV.
    public List<PositionAttribute> PositionAttributes { get; set; } = [];

    // Empty when IsPublic is true.
    public List<PositionAccessRule> AccessRules { get; set; } = [];

    // Only projects carrying at least one of these tags are pulled into the CV.
    public List<PositionProjectTag> ProjectTags { get; set; } = [];

    public List<Cv> Cvs { get; set; } = [];

    public List<DiscussionPost> DiscussionPosts { get; set; } = [];
}

// Which attributes the position asks for, and in what order they appear in the CV.
public class PositionAttribute
{
    public int PositionId { get; set; }
    public Position? Position { get; set; }

    public int AttributeId { get; set; }
    public LibraryAttribute? Attribute { get; set; }

    public int SortOrder { get; set; }
}

// Tags used to select which of the candidate's projects belong in this CV.
public class PositionProjectTag
{
    public int PositionId { get; set; }
    public Position? Position { get; set; }

    public int TagId { get; set; }
    public Tag? Tag { get; set; }
}

// One condition a candidate must satisfy to get access to a restricted position,
// e.g. attribute "IELTS Score" GreaterThan 7.0. A candidate needs to satisfy
// all of the rules, and they are evaluated in SQL (see PositionAccessService).
public class PositionAccessRule
{
    public int Id { get; set; }

    public int PositionId { get; set; }
    public Position? Position { get; set; }

    public int AttributeId { get; set; }
    public LibraryAttribute? Attribute { get; set; }

    public RuleOperator Operator { get; set; }

    // Same typed-column approach as AttributeValue, and for the same reason:
    // the comparison happens in the database against a column of the same type.
    public string? ValueString { get; set; }
    public decimal? ValueNumber { get; set; }
    public DateOnly? ValueDate { get; set; }
    public bool? ValueBoolean { get; set; }
    public int? ValueOptionId { get; set; }
}
