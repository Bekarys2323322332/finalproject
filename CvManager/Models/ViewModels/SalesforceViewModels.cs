using System.ComponentModel.DataAnnotations;

namespace CvManager.Models.ViewModels;

// The sync form. Everything on it is information the application does not
// otherwise store - the parts it already knows (name, email) are shown as plain
// text at the top of the page so the user can see exactly what is about to be
// sent without being able to edit it here.
public class SalesforceSyncViewModel
{
    // Set when an admin syncs somebody else's profile. Everybody else is pinned
    // to their own id in the controller no matter what arrives in this field.
    public string? OwnerId { get; set; }

    // Read-only context, filled by the controller from the non-removable
    // profile attributes and from Identity.
    public string DisplayName { get; set; } = "";

    public string Email { get; set; } = "";

    public string? LocationHint { get; set; }

    [Required(ErrorMessage = "A company name is required - it becomes the name of the Salesforce Account.")]
    [StringLength(255)]
    [Display(Name = "Company name")]
    public string CompanyName { get; set; } = "";

    // A Salesforce picklist rather than free text; see IndustryOptions below.
    [StringLength(255)]
    [Display(Name = "Industry")]
    public string? Industry { get; set; }

    [StringLength(255)]
    [Url(ErrorMessage = "Enter a full address including http:// or https://, or leave this empty.")]
    [Display(Name = "Company website")]
    public string? Website { get; set; }

    [StringLength(40)]
    [Phone(ErrorMessage = "That does not look like a phone number.")]
    [Display(Name = "Phone")]
    public string? Phone { get; set; }

    [StringLength(128)]
    [Display(Name = "Job title")]
    public string? JobTitle { get; set; }

    [StringLength(100)]
    [Display(Name = "City")]
    public string? City { get; set; }

    [StringLength(100)]
    [Display(Name = "Country")]
    public string? Country { get; set; }

    // --- Shown, never posted ---------------------------------------------

    // The records a previous sync created, so the page can say "you have
    // already synced" and link straight to them.
    public string? SalesforceAccountId { get; set; }

    public string? SalesforceContactId { get; set; }

    public DateTime? LastSyncedAt { get; set; }

    // The org URL, used to build the record links. Empty when the integration
    // has never successfully authenticated, in which case the ids are shown as
    // plain text instead of links.
    public string? OrgBaseUrl { get; set; }

    // False when the server has no Salesforce credentials configured. The form
    // is then shown disabled with an explanation, rather than letting somebody
    // fill it in and press a button that cannot work.
    public bool IsConfigured { get; set; }

    public bool HasSynced => !string.IsNullOrWhiteSpace(SalesforceAccountId);

    // The standard Salesforce Industry picklist.
    //
    // This is a dropdown and not a text box on purpose: Industry is a restricted
    // picklist in a stock org, so a free-text value like "Software" is rejected
    // with INVALID_OR_NULL_FOR_RESTRICTED_PICKLIST. Offering exactly the values
    // Salesforce ships with means the sync cannot fail for that reason.
    // An org that has customised the picklist would need this list adjusted to
    // match.
    public static readonly string[] IndustryOptions =
    [
        "Agriculture", "Apparel", "Banking", "Biotechnology", "Chemicals",
        "Communications", "Construction", "Consulting", "Education", "Electronics",
        "Energy", "Engineering", "Entertainment", "Environmental", "Finance",
        "Food & Beverage", "Government", "Healthcare", "Hospitality", "Insurance",
        "Machinery", "Manufacturing", "Media", "Not For Profit", "Other",
        "Recreation", "Retail", "Shipping", "Technology", "Telecommunications",
        "Transportation", "Utilities"
    ];
}
