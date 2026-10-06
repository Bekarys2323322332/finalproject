namespace CvManager.Services;

// Everything the Salesforce integration needs to know about the org it talks to.
// Bound from the "Salesforce" configuration section in Program.cs, which means
// the real values come from appsettings.Development.json locally (git-ignored)
// and from environment variables in Cloud Run. Nothing sensitive is ever
// compiled in or committed.
public class SalesforceOptions
{
    public const string SectionName = "Salesforce";

    // The org's My Domain base URL, for example
    // https://my-company-dev-ed.develop.my.salesforce.com
    //
    // This must be the My Domain URL and NOT login.salesforce.com. The Client
    // Credentials flow is only served from the org's own domain - pointing it at
    // the generic login host returns an unhelpful "invalid_client" error that
    // looks like a wrong secret.
    public string LoginUrl { get; set; } = "";

    // "Consumer Key" on the Connected App / External Client App.
    public string ClientId { get; set; } = "";

    // "Consumer Secret" on the same app.
    public string ClientSecret { get; set; } = "";

    // Which REST API version to call. Configurable rather than hardcoded so the
    // version can be bumped without a code change - ask the org what it
    // supports by opening {LoginUrl}/services/data/ in a browser.
    public string ApiVersion { get; set; } = "v62.0";

    // How long to reuse an access token before asking for a new one.
    //
    // The Client Credentials token response does not include an "expires_in"
    // field, so there is nothing to read the real lifetime from. The token
    // actually lives as long as the session timeout configured in the org
    // (two hours by default), so a conservative window plus the retry-on-401
    // path in SalesforceService is what keeps this correct rather than this
    // number being exact.
    public int TokenCacheMinutes { get; set; } = 30;

    // True when every required value is present. The profile page uses this to
    // explain that the integration is not configured instead of letting the
    // user fill in a form that cannot possibly succeed.
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(LoginUrl)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret);

    // LoginUrl with any trailing slash removed, so building request URLs never
    // produces a double slash.
    public string BaseUrl => LoginUrl.TrimEnd('/');
}
