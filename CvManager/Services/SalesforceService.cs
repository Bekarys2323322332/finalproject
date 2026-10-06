using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CvManager.Services;

// Everything the application wants to push into Salesforce for one user.
// A plain record so the controller can build it and hand it over without the
// service needing to know anything about ApplicationUser, HTTP or MVC.
public record SalesforceSyncData
{
    // From the non-removable ("system") profile attributes and Identity. These
    // are the fields the application guarantees always exist for every user.
    public string FirstName { get; init; } = "";
    public string LastName { get; init; } = "";
    public string Email { get; init; } = "";

    // From the sync form.
    public string CompanyName { get; init; } = "";
    public string? Industry { get; init; }
    public string? Website { get; init; }
    public string? Phone { get; init; }
    public string? JobTitle { get; init; }
    public string? City { get; init; }
    public string? Country { get; init; }

    // The ids of the records created by a previous sync, if there was one.
    // When these are set the service updates those records; when they are null
    // it creates new ones. This is what stops a second press of the button
    // producing a duplicate Account.
    public string? ExistingAccountId { get; init; }
    public string? ExistingContactId { get; init; }
}

// What came back. Either both ids, or a message that can be shown to the user
// as-is. The controller never has to look at an HTTP status code.
public record SalesforceSyncResult
{
    public bool Success { get; init; }
    public string? AccountId { get; init; }
    public string? ContactId { get; init; }
    public string? ErrorMessage { get; init; }

    // True when the records were created, false when existing ones were
    // updated. Only used to word the success message correctly.
    public bool Created { get; init; }

    public static SalesforceSyncResult Fail(string message) =>
        new() { Success = false, ErrorMessage = message };
}

// Talks to the Salesforce REST API. This is the only class in the project that
// knows Salesforce exists - the controller just hands it a SalesforceSyncData
// and gets a result back, which keeps the controller thin and means the whole
// integration can be reasoned about (or replaced) in one file.
//
// Two calls happen per sync:
//   1. POST /services/oauth2/token   - swap the client id and secret for an
//                                      access token (Client Credentials flow).
//   2. POST /services/data/{v}/composite/  - create or update the Account and
//                                      the Contact in a single request.
//
// Using the Composite API rather than two separate REST calls matters: with
// "allOrNone": true either both records are written or neither is, so a failure
// halfway through cannot leave an Account with no Contact attached to it. It
// also lets the Contact reference the brand new Account's id without the
// application having to read it back first.
public class SalesforceService
{
    private readonly HttpClient _http;
    private readonly SalesforceOptions _options;
    private readonly ILogger<SalesforceService> _logger;

    // The access token is cached so that a burst of syncs does not ask
    // Salesforce for a new one every time.
    //
    // These are static on purpose. The service is registered as a typed
    // HttpClient, which makes it transient - a new instance per injection - so
    // an instance field would cache nothing at all. The semaphore stops two
    // simultaneous requests both deciding the token is stale and fetching one
    // each.
    private static string? _cachedToken;
    private static string? _cachedInstanceUrl;
    private static DateTime _cachedTokenExpiresAt = DateTime.MinValue;
    private static readonly SemaphoreSlim TokenLock = new(1, 1);

    // Reference names used inside the composite request to wire the two
    // sub-requests together. Any string works; these are just readable.
    private const string AccountRef = "newAccount";
    private const string ContactRef = "newContact";

    public SalesforceService(HttpClient http, IOptions<SalesforceOptions> options,
        ILogger<SalesforceService> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured => _options.IsConfigured;

    // The org base URL, used by the view to build "open this record in
    // Salesforce" links next to the stored ids.
    public string OrgBaseUrl => _cachedInstanceUrl ?? _options.BaseUrl;

    // The one public entry point. Creates or updates the Account and Contact and
    // never throws: every failure comes back as a result with a message that is
    // safe to show to the user.
    public async Task<SalesforceSyncResult> SyncAsync(SalesforceSyncData data,
        CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
        {
            return SalesforceSyncResult.Fail(
                "The Salesforce integration is not configured on this server. "
                + "The client id, client secret and login URL all have to be set first.");
        }

        try
        {
            // First attempt: use whatever token is cached, and update the
            // existing records if we have their ids.
            var attempt = await SendCompositeAsync(data, useExistingIds: true,
                forceNewToken: false, cancellationToken);

            // The cached token was rejected. Salesforce does not tell us how
            // long a Client Credentials token lives, so rather than trying to
            // predict expiry this path simply throws the token away, gets a
            // fresh one and sends the request again. One retry only - if a
            // brand new token is also refused, the credentials themselves are
            // wrong and retrying forever would not help.
            if (attempt.Unauthorized)
            {
                _logger.LogInformation(
                    "Salesforce rejected the cached access token; fetching a new one and retrying once.");

                attempt = await SendCompositeAsync(data, useExistingIds: true,
                    forceNewToken: true, cancellationToken);
            }

            // Somebody deleted the records in Salesforce by hand, so the ids we
            // stored point at nothing. Falling back to a create is much friendlier
            // than showing the user an "entity is deleted" error they can do
            // nothing about. The controller overwrites the stale ids with the
            // new ones it gets back.
            if (attempt.RecordsGone)
            {
                _logger.LogInformation(
                    "The stored Salesforce record ids no longer exist in the org; creating new records instead.");

                attempt = await SendCompositeAsync(data, useExistingIds: false,
                    forceNewToken: false, cancellationToken);
            }

            return attempt.Result;
        }
        catch (TaskCanceledException)
        {
            // Covers the HttpClient timeout as well as a cancelled request.
            return SalesforceSyncResult.Fail(
                "Salesforce did not respond in time. Please try again in a moment.");
        }
        catch (HttpRequestException ex)
        {
            // No network, DNS failure, TLS problem, wrong My Domain host.
            _logger.LogError(ex, "Could not reach Salesforce.");
            return SalesforceSyncResult.Fail(
                "Could not reach Salesforce. Check the login URL and that the server has internet access.");
        }
        catch (JsonException ex)
        {
            // Salesforce answered with something that is not the JSON we expect,
            // which usually means a login page was returned instead of the API.
            _logger.LogError(ex, "Salesforce returned a response that could not be read.");
            return SalesforceSyncResult.Fail(
                "Salesforce returned an unexpected response. Check that the login URL is the org's My Domain URL.");
        }
    }

    // What one attempt produced, plus the two conditions that make it worth
    // trying again with different settings.
    private record CompositeAttempt(SalesforceSyncResult Result, bool Unauthorized, bool RecordsGone);

    private async Task<CompositeAttempt> SendCompositeAsync(SalesforceSyncData data, bool useExistingIds,
        bool forceNewToken, CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(forceNewToken, cancellationToken);
        if (token is null)
        {
            return new CompositeAttempt(
                SalesforceSyncResult.Fail(
                    "Salesforce refused the client id and secret. Check the Consumer Key and Consumer Secret, "
                    + "and that the Client Credentials flow is enabled on the connected app with a Run As user set."),
                Unauthorized: false, RecordsGone: false);
        }

        var accountId = useExistingIds ? Trimmed(data.ExistingAccountId) : null;
        var contactId = useExistingIds ? Trimmed(data.ExistingContactId) : null;
        var isCreate = accountId is null && contactId is null;

        var body = BuildCompositeBody(data, accountId, contactId);

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{OrgBaseUrl}/services/data/{_options.ApiVersion}/composite/");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        // A 401 on the composite call means the token is no longer good. The
        // caller decides whether to retry.
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            InvalidateToken();
            return new CompositeAttempt(
                SalesforceSyncResult.Fail("The Salesforce access token was rejected."),
                Unauthorized: true, RecordsGone: false);
        }

        if (!response.IsSuccessStatusCode)
        {
            // The composite endpoint itself failed, for example because the API
            // version in configuration does not exist in this org.
            _logger.LogError("Salesforce composite request failed with {Status}: {Body}",
                (int)response.StatusCode, json);

            return new CompositeAttempt(
                SalesforceSyncResult.Fail(DescribeTransportFailure(response.StatusCode, json)),
                Unauthorized: false, RecordsGone: false);
        }

        return ReadCompositeResponse(json, accountId, contactId, isCreate);
    }

    // Builds the composite request body.
    //
    // Each of the two sub-requests is a POST when we have no id for that record
    // and a PATCH when we do. The interesting part is how the Contact finds its
    // Account:
    //
    //   - On a create, the Account does not have an id yet, so the Contact's
    //     AccountId is written as "@{newAccount.id}". Salesforce substitutes the
    //     real id from the first sub-request before running the second one. This
    //     is exactly why the Composite API is used here instead of two calls.
    //
    //   - On an update, the id is already known, so it is sent literally. This
    //     also repairs the link if the Contact ever got detached.
    //
    // "allOrNone": true makes the whole thing atomic. Without it a valid Account
    // plus an invalid Contact would leave a company record in the CRM with
    // nobody attached to it.
    private string BuildCompositeBody(SalesforceSyncData data, string? accountId, string? contactId)
    {
        var version = _options.ApiVersion;

        // Account: the company, from the form.
        var accountFields = new Dictionary<string, object?>
        {
            ["Name"] = data.CompanyName.Trim()
        };

        AddIfPresent(accountFields, "Phone", data.Phone);
        AddIfPresent(accountFields, "Website", data.Website);
        AddIfPresent(accountFields, "Industry", data.Industry);
        AddIfPresent(accountFields, "BillingCity", data.City);
        AddIfPresent(accountFields, "BillingCountry", data.Country);

        // Contact: the person. LastName is the only field Salesforce insists on,
        // which is why it falls back to the email local part - a user who has
        // not filled in their name yet should still be able to sync rather than
        // being shown a Salesforce validation error.
        var contactFields = new Dictionary<string, object?>
        {
            ["LastName"] = FallbackLastName(data),
            ["AccountId"] = accountId ?? $"@{{{AccountRef}.id}}"
        };

        AddIfPresent(contactFields, "FirstName", data.FirstName);
        AddIfPresent(contactFields, "Email", data.Email);
        AddIfPresent(contactFields, "Title", data.JobTitle);
        AddIfPresent(contactFields, "Phone", data.Phone);
        AddIfPresent(contactFields, "MailingCity", data.City);
        AddIfPresent(contactFields, "MailingCountry", data.Country);

        var payload = new Dictionary<string, object?>
        {
            ["allOrNone"] = true,
            ["compositeRequest"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["method"] = accountId is null ? "POST" : "PATCH",
                    ["url"] = accountId is null
                        ? $"/services/data/{version}/sobjects/Account"
                        : $"/services/data/{version}/sobjects/Account/{accountId}",
                    ["referenceId"] = AccountRef,
                    ["body"] = accountFields
                },
                new Dictionary<string, object?>
                {
                    ["method"] = contactId is null ? "POST" : "PATCH",
                    ["url"] = contactId is null
                        ? $"/services/data/{version}/sobjects/Contact"
                        : $"/services/data/{version}/sobjects/Contact/{contactId}",
                    ["referenceId"] = ContactRef,
                    ["body"] = contactFields
                }
            }
        };

        return JsonSerializer.Serialize(payload);
    }

    // Reads the composite response and works out which of three things happened:
    // it worked, the stored ids are stale, or Salesforce rejected the data.
    //
    // The response contains one entry per sub-request, each with its own status
    // code and body. A successful POST returns 201 and a body holding the new
    // id; a successful PATCH returns 204 and no body at all, which is why the
    // ids we already had are carried through as the answer in that case.
    private CompositeAttempt ReadCompositeResponse(string json, string? accountId, string? contactId, bool isCreate)
    {
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("compositeResponse", out var entries))
        {
            return new CompositeAttempt(
                SalesforceSyncResult.Fail("Salesforce returned a response without any results in it."),
                Unauthorized: false, RecordsGone: false);
        }

        var resultAccountId = accountId;
        var resultContactId = contactId;
        var errors = new List<string>();
        var recordsGone = false;

        foreach (var entry in entries.EnumerateArray())
        {
            var status = entry.TryGetProperty("httpStatusCode", out var statusElement)
                ? statusElement.GetInt32()
                : 0;

            var reference = entry.TryGetProperty("referenceId", out var referenceElement)
                ? referenceElement.GetString()
                : null;

            var hasBody = entry.TryGetProperty("body", out var bodyElement)
                && bodyElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

            if (status is >= 200 and < 300)
            {
                // A create returns the new id; an update returns nothing, so the
                // id already in hand stays as it is.
                if (hasBody && bodyElement.ValueKind == JsonValueKind.Object
                    && bodyElement.TryGetProperty("id", out var idElement))
                {
                    var newId = idElement.GetString();

                    if (reference == AccountRef)
                    {
                        resultAccountId = newId;
                    }
                    else if (reference == ContactRef)
                    {
                        resultContactId = newId;
                    }
                }

                continue;
            }

            // This sub-request failed. Its body is an array of Salesforce error
            // objects, each with a message and an error code.
            if (!hasBody)
            {
                errors.Add($"Salesforce returned status {status} without explaining why.");
                continue;
            }

            foreach (var (message, code) in ReadErrors(bodyElement))
            {
                // With allOrNone the sibling sub-request is reported as halted
                // rather than failed. Showing that to the user would bury the
                // real cause, so it is only used if nothing better turns up.
                if (code == "PROCESSING_HALTED")
                {
                    continue;
                }

                if (IsMissingRecordCode(code) || status == 404)
                {
                    recordsGone = true;
                }

                errors.Add(Humanise(message, code));
            }
        }

        // The ids point at records that are no longer in the org. Signal that so
        // the caller can retry as a create; the message is not shown.
        if (recordsGone && !isCreate)
        {
            return new CompositeAttempt(
                SalesforceSyncResult.Fail("The stored Salesforce records no longer exist."),
                Unauthorized: false, RecordsGone: true);
        }

        if (errors.Count > 0)
        {
            _logger.LogWarning("Salesforce rejected the sync: {Errors}", string.Join(" | ", errors));

            return new CompositeAttempt(
                SalesforceSyncResult.Fail("Salesforce rejected the data: " + string.Join(" ", errors.Distinct())),
                Unauthorized: false, RecordsGone: false);
        }

        // Both sub-requests succeeded but no ids came back, which should not be
        // possible. Reported rather than silently storing nulls.
        if (string.IsNullOrWhiteSpace(resultAccountId) || string.IsNullOrWhiteSpace(resultContactId))
        {
            return new CompositeAttempt(
                SalesforceSyncResult.Fail("Salesforce reported success but did not return the record ids."),
                Unauthorized: false, RecordsGone: false);
        }

        return new CompositeAttempt(
            new SalesforceSyncResult
            {
                Success = true,
                AccountId = resultAccountId,
                ContactId = resultContactId,
                Created = isCreate
            },
            Unauthorized: false, RecordsGone: false);
    }

    // Gets an access token using the OAuth 2.0 Client Credentials flow.
    //
    // This flow is server-to-server: there is no user being redirected to a
    // Salesforce login page. The application sends its client id and secret and
    // gets back a token that acts as the "Run As" user configured on the
    // connected app. That is the right shape for a background push like this one,
    // and it is why every record in Salesforce will be owned by that single
    // integration user rather than by the person who pressed the button.
    //
    // Returns null when Salesforce refuses the credentials, which the caller
    // turns into a readable message.
    private async Task<string?> GetAccessTokenAsync(bool forceNewToken, CancellationToken cancellationToken)
    {
        if (!forceNewToken && _cachedToken is not null && DateTime.UtcNow < _cachedTokenExpiresAt)
        {
            return _cachedToken;
        }

        await TokenLock.WaitAsync(cancellationToken);
        try
        {
            // Checked again inside the lock: while this request was waiting,
            // another one may already have fetched a perfectly good token.
            if (!forceNewToken && _cachedToken is not null && DateTime.UtcNow < _cachedTokenExpiresAt)
            {
                return _cachedToken;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{_options.BaseUrl}/services/oauth2/token");

            // The token endpoint takes form-encoded values, not JSON.
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret
            });

            using var response = await _http.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Salesforce answers with {"error": "...", "error_description": "..."}.
                // The secret is never logged; only the reason.
                _logger.LogError("Salesforce token request failed with {Status}: {Body}",
                    (int)response.StatusCode, json);

                InvalidateToken();
                return null;
            }

            using var document = JsonDocument.Parse(json);
            var token = document.RootElement.TryGetProperty("access_token", out var tokenElement)
                ? tokenElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(token))
            {
                InvalidateToken();
                return null;
            }

            // Salesforce returns the URL that API calls should actually go to.
            // It is normally the same as the My Domain URL, but using what the
            // token response says is the documented behaviour and survives an
            // org being migrated.
            if (document.RootElement.TryGetProperty("instance_url", out var instanceElement))
            {
                var instanceUrl = instanceElement.GetString();
                if (!string.IsNullOrWhiteSpace(instanceUrl))
                {
                    _cachedInstanceUrl = instanceUrl.TrimEnd('/');
                }
            }

            _cachedToken = token;
            _cachedTokenExpiresAt = DateTime.UtcNow.AddMinutes(Math.Max(1, _options.TokenCacheMinutes));

            return token;
        }
        finally
        {
            TokenLock.Release();
        }
    }

    private static void InvalidateToken()
    {
        _cachedToken = null;
        _cachedTokenExpiresAt = DateTime.MinValue;
    }

    // Pulls (message, errorCode) pairs out of an error body, which Salesforce
    // sends as an array but occasionally as a single object.
    private static IEnumerable<(string Message, string? Code)> ReadErrors(JsonElement body)
    {
        if (body.ValueKind == JsonValueKind.Array)
        {
            foreach (var error in body.EnumerateArray())
            {
                yield return ReadOneError(error);
            }
        }
        else if (body.ValueKind == JsonValueKind.Object)
        {
            yield return ReadOneError(body);
        }
    }

    private static (string Message, string? Code) ReadOneError(JsonElement error)
    {
        var message = error.TryGetProperty("message", out var messageElement)
            ? messageElement.GetString()
            : null;

        var code = error.TryGetProperty("errorCode", out var codeElement)
            ? codeElement.GetString()
            : null;

        return (message ?? "Salesforce reported an error without a message.", code);
    }

    // The error codes that all mean "the record you pointed at is not there any
    // more", which is the signal to create instead of update.
    private static bool IsMissingRecordCode(string? code) => code is
        "NOT_FOUND"
        or "ENTITY_IS_DELETED"
        or "INVALID_CROSS_REFERENCE_KEY";

    // Turns the more cryptic Salesforce error codes into something a user can
    // act on. Anything not listed is passed through unchanged - Salesforce
    // messages are usually readable enough on their own.
    private static string Humanise(string message, string? code) => code switch
    {
        "REQUIRED_FIELD_MISSING" =>
            $"A field Salesforce requires was empty. {message}",

        "INVALID_OR_NULL_FOR_RESTRICTED_PICKLIST" =>
            "One of the values is not on the list Salesforce allows for that field "
            + $"(most often Industry, or Country when State and Country picklists are switched on). {message}",

        "STRING_TOO_LONG" =>
            $"One of the values is longer than Salesforce accepts. {message}",

        "INVALID_EMAIL_ADDRESS" =>
            $"Salesforce would not accept the email address. {message}",

        "DUPLICATES_DETECTED" =>
            "Salesforce has a duplicate rule that matched an existing record. "
            + "An administrator can allow the save or adjust the rule.",

        "INSUFFICIENT_ACCESS_ON_CROSS_REFERENCE_ENTITY" or "INSUFFICIENT_ACCESS_OR_READONLY" =>
            "The Salesforce user this integration runs as does not have permission to write these records. "
            + "Check the Run As user's profile on the connected app.",

        _ => message
    };

    // Explains a failure of the composite endpoint itself, as opposed to a
    // rejection of the data inside it.
    private string DescribeTransportFailure(HttpStatusCode status, string body)
    {
        if (status == HttpStatusCode.NotFound)
        {
            return $"Salesforce does not recognise API version {_options.ApiVersion}. "
                   + $"Open {_options.BaseUrl}/services/data/ to see which versions this org supports.";
        }

        if (status == HttpStatusCode.Forbidden)
        {
            return "Salesforce refused the request. The most common cause is that the connected app's "
                   + "OAuth scopes do not include API access, or an IP restriction is blocking the server.";
        }

        // Fall back to whatever Salesforce said, if it is short enough to show.
        using var document = JsonDocument.Parse(body);
        var errors = ReadErrors(document.RootElement).Select(e => e.Message).ToList();

        return errors.Count > 0
            ? "Salesforce returned an error: " + string.Join(" ", errors)
            : $"Salesforce returned status {(int)status}.";
    }

    // Only writes a field when there is something to write. Sending an empty
    // string would blank out a value an administrator had filled in by hand in
    // Salesforce, which is not what "sync my details" should mean.
    private static void AddIfPresent(Dictionary<string, object?> fields, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            fields[name] = value.Trim();
        }
    }

    // LastName is the only genuinely required field on a Salesforce Contact.
    // A user who has not filled in their name yet still gets something sensible
    // rather than a validation error from the CRM.
    private static string FallbackLastName(SalesforceSyncData data)
    {
        if (!string.IsNullOrWhiteSpace(data.LastName))
        {
            return data.LastName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(data.FirstName))
        {
            return data.FirstName.Trim();
        }

        var email = data.Email;
        if (!string.IsNullOrWhiteSpace(email))
        {
            var localPart = email.Split('@')[0];
            if (!string.IsNullOrWhiteSpace(localPart))
            {
                return localPart;
            }
        }

        return "Unknown";
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
