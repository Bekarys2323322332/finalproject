namespace CvManager.Models.ViewModels;

// What the browser sends for one attribute value. The version is the one the
// page was rendered with - it is the whole point of the exchange.
public class ValueDto
{
    public int AttributeId { get; set; }
    public int Version { get; set; }

    public string? ValueString { get; set; }
    public decimal? ValueNumber { get; set; }
    public DateOnly? ValueDate { get; set; }
    public DateOnly? ValueDateEnd { get; set; }
    public bool? ValueBoolean { get; set; }
    public int? ValueOptionId { get; set; }

    // The client says explicitly whether the field was filled in, because an
    // empty string, a zero and a false are all real answers.
    public bool HasValue { get; set; }
}

public class SaveValuesRequest
{
    // Admins may save on behalf of a candidate; for everybody else this is
    // ignored and the signed-in user is used.
    public string? UserId { get; set; }

    public List<ValueDto> Items { get; set; } = [];
}

// The answer for one value: either it saved and here is the new version to use
// for the next save, or somebody else changed it and here is their value so the
// client can show it instead of silently overwriting.
public class ValueResult
{
    public int AttributeId { get; set; }
    public int Version { get; set; }
    public bool Conflict { get; set; }

    public string? ValueString { get; set; }
    public decimal? ValueNumber { get; set; }
    public DateOnly? ValueDate { get; set; }
    public DateOnly? ValueDateEnd { get; set; }
    public bool? ValueBoolean { get; set; }
    public int? ValueOptionId { get; set; }
    public bool HasValue { get; set; }
}

public class SaveValuesResponse
{
    public List<ValueResult> Results { get; set; } = [];
    public bool AnyConflict => Results.Any(r => r.Conflict);
}
