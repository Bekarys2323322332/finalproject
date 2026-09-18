using NpgsqlTypes;

namespace CvManager.Models;

// Just a lookup table for grouping/filtering attributes in the UI. It has no
// effect on the data model or on validation - a recruiter may put "Dancing
// Skills" in "Certification" and nothing stops them. Seeded in the DbContext;
// there is deliberately no UI to edit categories, they are changed in the DB.
public class AttributeCategory
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public List<LibraryAttribute> Attributes { get; set; } = [];
}

// One entry of the shared attribute library. Every recruiter works with the same
// pool, so there is no owner column here.
// Named LibraryAttribute rather than Attribute because "Attribute" is taken by
// System.Attribute and it would be confusing to read.
public class LibraryAttribute : IVersioned
{
    public int Id { get; set; }

    // Must be globally unique. I do not check this in code - there is a unique
    // index on this column and I catch the resulting DbUpdateException instead,
    // otherwise two recruiters saving the same name at the same moment could
    // both pass a check-then-insert.
    public string Name { get; set; } = "";

    public int CategoryId { get; set; }
    public AttributeCategory? Category { get; set; }

    public string? Description { get; set; }

    public AttributeType Type { get; set; }

    // True for the four built-in profile attributes (First Name, Last Name,
    // Location, Photo). They live in the library like everything else, so they
    // can be put on a position template, but recruiters cannot delete or retype
    // them - the controller refuses, and every candidate always has them.
    public bool IsSystem { get; set; }

    // Only used to keep the built-in "Me" fields in a sensible order.
    public int SortOrder { get; set; }

    // Stamped whenever this attribute is put on a position or added to a
    // profile. The library gets large, so the attribute picker offers the
    // recently used ones first and this is what it sorts by.
    public DateTime? LastUsedAt { get; set; }

    // Optimistic locking. Sent to the client, sent back on save, and the UPDATE
    // only matches if it is unchanged. See ApplicationDbContext for the wiring.
    public int Version { get; set; }

    // Only filled for Type == Dropdown.
    public List<AttributeOption> Options { get; set; } = [];

    public List<AttributeValue> Values { get; set; } = [];
}

// One choice of a dropdown attribute, e.g. None / Essentials / Pro / Expert.
public class AttributeOption
{
    public int Id { get; set; }

    public int AttributeId { get; set; }
    public LibraryAttribute? Attribute { get; set; }

    public string Value { get; set; } = "";

    public int SortOrder { get; set; }
}

// A candidate's answer for one attribute. One row per (user, attribute) - the
// unique index enforces it - and this row is the only place the value exists.
// CVs do not copy it.
public class AttributeValue : IVersioned
{
    public int Id { get; set; }

    public string UserId { get; set; } = "";
    public ApplicationUser? User { get; set; }

    public int AttributeId { get; set; }
    public LibraryAttribute? Attribute { get; set; }

    // I use one typed column per kind of value instead of a single text column.
    // It costs a few mostly-empty columns, but it lets the access rules compare
    // numbers as numbers and dates as dates in SQL ("GPA > 3.5" is a real
    // numeric comparison on an indexable column, not a cast of a string).

    // String, Text (markdown source) and Image (the Cloudinary URL).
    public string? ValueString { get; set; }

    public decimal? ValueNumber { get; set; }

    // Date, and the start of a Period.
    public DateOnly? ValueDate { get; set; }

    // End of a Period. Null means "still ongoing".
    public DateOnly? ValueDateEnd { get; set; }

    public bool? ValueBoolean { get; set; }

    // Dropdown. I store the option id rather than its text, so renaming an
    // option does not silently invalidate everybody's stored answer.
    public int? ValueOptionId { get; set; }
    public AttributeOption? ValueOption { get; set; }

    public int Version { get; set; }

    // True when the candidate has actually answered. Needed because "0", "false"
    // and an empty string are all legitimate answers, so I cannot infer
    // emptiness from the columns alone - and the CV has to paint unanswered
    // attributes red.
    public bool HasValue { get; set; }

    // Generated from ValueString, GIN indexed. This is what makes the header
    // search able to find a candidate by name or by the text they wrote,
    // because names and free text are themselves just attribute values here.
    public NpgsqlTsVector? SearchVector { get; set; }
}
