using System.ComponentModel.DataAnnotations;

namespace CvManager.Models.ViewModels;

// The attribute library table: the rows plus the state of the search box,
// category filter and pager, so the view can render them back.
public class AttributeIndexViewModel
{
    public List<LibraryAttribute> Items { get; init; } = [];
    public List<AttributeCategory> Categories { get; init; } = [];

    public string? Query { get; init; }
    public int? CategoryId { get; init; }

    public int Page { get; init; } = 1;
    public int TotalPages { get; init; } = 1;
}

public class AttributeEditViewModel
{
    public int Id { get; set; }

    [Required, StringLength(200)]
    [Display(Name = "Name")]
    public string Name { get; set; } = "";

    [Required]
    [Display(Name = "Category")]
    public int CategoryId { get; set; }

    [StringLength(1000)]
    [Display(Name = "Description")]
    public string? Description { get; set; }

    [Display(Name = "Type")]
    public AttributeType Type { get; set; }

    // Dropdown choices, one per line. A textarea keeps this simple - the
    // controller splits the lines into AttributeOption rows.
    [Display(Name = "Options (one per line)")]
    public string? OptionsText { get; set; }

    // Carried through the form so the save can detect that somebody else edited
    // this attribute in the meantime.
    public int Version { get; set; }

    // Built-in attributes can be renamed and re-described but not retyped or
    // deleted, so the form disables the type selector for them.
    public bool IsSystem { get; set; }

    public List<AttributeCategory> Categories { get; set; } = [];
}
