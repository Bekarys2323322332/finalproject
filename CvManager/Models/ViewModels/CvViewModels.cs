using CvManager.Models;

namespace CvManager.Models.ViewModels;

// One line of a rendered CV: the attribute the position asked for, plus the
// candidate's answer if they have one. Value is null when the candidate has
// never filled this attribute in, which is what the red highlight keys off.
public class CvAttributeRow
{
    public required LibraryAttribute Attribute { get; init; }

    public AttributeValue? Value { get; init; }

    public bool HasValue => Value is { HasValue: true };

    // Read-only text for the rendered CV. Edit mode uses the raw columns
    // instead, because an input needs the value, not a formatted string.
    public string DisplayText
    {
        get
        {
            if (!HasValue || Value is null)
            {
                return string.Empty;
            }

            return Attribute.Type switch
            {
                AttributeType.Boolean => Value.ValueBoolean == true ? "Yes" : "No",
                AttributeType.Numeric => Value.ValueNumber?.ToString("0.####") ?? string.Empty,
                AttributeType.Date => Value.ValueDate?.ToString("d MMM yyyy") ?? string.Empty,
                AttributeType.Period => FormatPeriod(Value.ValueDate, Value.ValueDateEnd),
                AttributeType.Dropdown => Value.ValueOption?.Value ?? string.Empty,
                _ => Value.ValueString ?? string.Empty
            };
        }
    }

    private static string FormatPeriod(DateOnly? from, DateOnly? to)
    {
        if (from is null)
        {
            return string.Empty;
        }

        var end = to?.ToString("MMM yyyy") ?? "present";
        return $"{from.Value:MMM yyyy} - {end}";
    }
}

// Everything the CV page needs. Assembled by CvBuilder on each request - none of
// this is stored on the Cv row, which is the whole point of the design.
public class CvViewModel
{
    public required Cv Cv { get; init; }
    public required Position Position { get; init; }
    public required ApplicationUser Candidate { get; init; }

    // The attributes the position asks for, in the order the recruiter set.
    // These are the ones that decide whether the CV may be published.
    public required List<CvAttributeRow> Attributes { get; init; }

    // The built-in profile attributes (name, location, photo). A generated CV
    // always shows these in its header, whether or not the recruiter put them on
    // the template, because they are what identifies the candidate.
    public List<CvAttributeRow> HeaderAttributes { get; init; } = [];

    // The candidate's projects that match the position's tag filter, newest
    // first, already cut down to the position's MaxProjects.
    public required List<Project> Projects { get; init; }

    public int LikeCount { get; init; }
    public bool LikedByCurrentUser { get; init; }

    // The candidate who owns it and admins may edit in place; recruiters only read.
    public bool CanEdit { get; init; }

    // Publish is only allowed when nothing is missing.
    public bool IsComplete => Attributes.All(a => a.HasValue);

    // Pulled out of the attribute rows for the CV header.
    public string? FirstName => TextOf(ApplicationDbContextIds.FirstName);
    public string? LastName => TextOf(ApplicationDbContextIds.LastName);
    public string? Location => TextOf(ApplicationDbContextIds.Location);
    public string? PhotoUrl => TextOf(ApplicationDbContextIds.Photo);

    public string FullName
    {
        get
        {
            var name = $"{FirstName} {LastName}".Trim();
            return name.Length > 0 ? name : Candidate.Email ?? "Candidate";
        }
    }

    private string? TextOf(int attributeId)
    {
        // Looks in the header first, then in the template, so it works whether or
        // not the recruiter asked for the field as well.
        var row = HeaderAttributes.FirstOrDefault(a => a.Attribute.Id == attributeId)
                  ?? Attributes.FirstOrDefault(a => a.Attribute.Id == attributeId);

        return row is { HasValue: true } ? row.Value?.ValueString : null;
    }
}

// The built-in attribute ids, duplicated here so the view model does not have to
// reference the DbContext just to read four constants.
public static class ApplicationDbContextIds
{
    public const int FirstName = 1;
    public const int LastName = 2;
    public const int Location = 3;
    public const int Photo = 4;
}
