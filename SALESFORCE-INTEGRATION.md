# Salesforce CRM Integration

The user profile page carries a "Sync to Salesforce" action. It opens a form,
collects details the application does not otherwise hold, and creates an
**Account** (the company) with a linked **Contact** (the person) in a Salesforce
org through the REST API. The Contact also carries the profile fields the
application guarantees always exist.

Syncing a second time updates those same two records rather than creating
duplicates.

---

## 1. Where the code lives

| File | Responsibility |
|---|---|
| `Services/SalesforceService.cs` | The only class aware that Salesforce exists. Token acquisition and caching, the Composite API request, response parsing, and the translation of every failure into a message safe to show a user. |
| `Services/SalesforceOptions.cs` | Login URL, client id, client secret, API version, token cache window. Bound from the `Salesforce` configuration section. |
| `Controllers/SalesforceController.cs` | Resolves whose profile is being synced, authorises the caller, assembles the payload, persists the returned record ids. Contains no HTTP or JSON. |
| `Models/ViewModels/SalesforceViewModels.cs` | Form shape, validation rules, and the standard Salesforce Industry picklist values. |
| `Views/Salesforce/Sync.cshtml` | The page: a read-only summary of what will be taken from the profile, then the form for the rest. |
| `Models/ApplicationUser.cs` | Ten added columns: the two Salesforce record ids and a sync timestamp, plus the seven form values retained so the form pre-fills on later visits. |
| `Migrations/20260930155746_SalesforceSync.cs` | Adds those columns to `AspNetUsers`. All nullable, so it applies to existing rows without data loss. |

The integration follows the same layering as the rest of the application:
views render, controllers authorise and translate HTTP, services hold logic, and
`ApplicationDbContext` owns schema decisions.

---

## 2. How a sync works

### Authorisation

`SalesforceController.ResolveTarget` decides whose profile is in play:

- no owner id, or the caller's own id, resolves to the caller
- any other id requires the Admin role, otherwise the request is refused

The rule is applied on both the GET and the POST. The button on the profile page
is wrapped in `@if (Model.CanEdit)`, but that is presentation only: a request
constructed by hand against another user's id is rejected by the controller.

### Assembling the payload

First name, last name and email come from the profile rather than the form.
In this application a name is not a column but an attribute value, and these
particular attributes are flagged `IsSystem`, which the profile page refuses to
delete. They are therefore the non-removable fields the task calls for, and
every user is guaranteed to have them.

The form supplies company name, industry, website, phone, job title, city and
country.

### Authentication

`POST {LoginUrl}/services/oauth2/token` with `grant_type=client_credentials`
and the connected app's consumer key and secret. Salesforce returns an access
token and the `instance_url` to direct API calls at.

The token is cached for thirty minutes in a static field, guarded by a
`SemaphoreSlim` so concurrent syncs do not each fetch one. The field is static
because the service is registered as a typed `HttpClient` and therefore
transient; an instance field would cache nothing.

### Writing to Salesforce

A single request to `POST {instance_url}/services/data/{version}/composite/`
carrying both records:

```json
{
  "allOrNone": true,
  "compositeRequest": [
    {
      "method": "POST",
      "url": "/services/data/v62.0/sobjects/Account",
      "referenceId": "newAccount",
      "body": {
        "Name": "Acme Corp.", "Industry": "Technology",
        "Phone": "+48 123 456 789", "Website": "https://example.com",
        "BillingCity": "Krakow", "BillingCountry": "Poland"
      }
    },
    {
      "method": "POST",
      "url": "/services/data/v62.0/sobjects/Contact",
      "referenceId": "newContact",
      "body": {
        "LastName": "Demo", "FirstName": "Alex",
        "Email": "cand@test.local", "Title": "Data Engineer",
        "MailingCity": "Krakow", "MailingCountry": "Poland",
        "AccountId": "@{newAccount.id}"
      }
    }
  ]
}
```

Two properties of this request are the reason the Composite API was chosen over
two sequential REST calls:

- **`"AccountId": "@{newAccount.id}"`** — the Account has no id at the moment
  the request is built. Salesforce executes the first sub-request and
  substitutes the resulting id into the second before executing it, so the
  application never has to create the Account, read its id back, and then create
  the Contact.
- **`"allOrNone": true`** — the pair is atomic. Without it, a valid Account
  followed by an invalid Contact would leave an orphaned company record in the
  CRM with nothing attached to it.

### Persisting the result

On success the Account and Contact ids and a timestamp are written to the user
row, together with the submitted form values. The confirmation message names
both record ids, which makes the result verifiable against the org rather than
merely asserted.

---

## 3. Repeat syncs

The stored record ids are what make a second sync safe. When they are present
each sub-request becomes a `PATCH` against `/sobjects/Account/{id}` and
`/sobjects/Contact/{id}`, and `AccountId` is sent literally instead of as a
reference. The same two records are updated and no duplicate Account appears.

If those records have since been deleted in Salesforce, the `PATCH` fails with
`NOT_FOUND` or `ENTITY_IS_DELETED`. The service recognises those specific codes,
discards the stale ids and retries the request as a create, so the user sees an
ordinary success rather than an error they cannot act on.

---

## 4. Failure handling

`SalesforceService.SyncAsync` does not throw. Every outcome returns a result
object carrying either the two ids or a message fit to display.

| Condition | Behaviour |
|---|---|
| Cached token rejected (401) | Discard the token, acquire a new one, retry once. A second rejection means the credentials are wrong, and further retries would not help. |
| Stored ids refer to deleted records | Clear them and retry as a create. |
| Salesforce rejects the data | The error code is mapped to a readable explanation and shown on the form with the submitted values preserved. Codes handled explicitly include `REQUIRED_FIELD_MISSING`, `INVALID_OR_NULL_FOR_RESTRICTED_PICKLIST`, `STRING_TOO_LONG`, `INVALID_EMAIL_ADDRESS`, `DUPLICATES_DETECTED` and the two insufficient-access codes. |
| Unknown API version (404 on the composite endpoint) | Reported together with the location of the org's version list. |
| Missing OAuth scope or IP restriction (403) | Reported with the likely cause named. |
| Network failure, timeout, unparseable response | Caught and reported; the HTTP timeout is thirty seconds, since a user is waiting. |
| No credentials configured | The form renders disabled with an explanation instead of accepting input that cannot succeed. |

Empty fields are omitted from the request rather than sent as empty strings, so
a sync does not blank out a value an administrator has filled in by hand in
Salesforce.

`LastName` is the only field Salesforce genuinely requires on a Contact. A user
who has not yet completed their profile would otherwise be blocked by a CRM
validation error, so the service falls back to the first name, then to the local
part of the email address.

---

## 5. Configuration

| Key | Purpose |
|---|---|
| `Salesforce:LoginUrl` | The org's My Domain base URL. The Client Credentials flow is served only from the org's own domain, not from the generic login host. |
| `Salesforce:ClientId` | Connected app consumer key. |
| `Salesforce:ClientSecret` | Connected app consumer secret. |
| `Salesforce:ApiVersion` | REST API version, configurable rather than compiled in. Defaults to `v62.0`. |
| `Salesforce:TokenCacheMinutes` | Token reuse window. Defaults to 30. |

The tracked `appsettings.json` carries this section with empty values, so the
required shape is documented in the repository while no credential is. Real
values are supplied from `appsettings.Development.json`, which is git-ignored,
and from environment variables in the deployed environment.

The connected app requires the Client Credentials flow to be enabled and a
"Run As" user selected; neither is on by default.

---

## 6. Design decisions

**The Client Credentials flow.** This is a server-to-server push with no user
present at a browser, so an interactive authorisation flow would be the wrong
shape. The trade-off is that the application authenticates as one fixed "Run As"
user, and every record in the org is therefore owned by that user rather than by
the person who triggered the sync. Per-user ownership would require each user to
hold their own Salesforce login and the authorisation code flow.

**Industry as a dropdown.** `Industry` is a restricted picklist on a stock
Salesforce Account, so free text is rejected with
`INVALID_OR_NULL_FOR_RESTRICTED_PICKLIST`. The form offers exactly the standard
values, which removes that failure entirely. The coupling is accepted
deliberately: an org with a customised picklist would need the list in
`SalesforceViewModels.cs` adjusted to match.

**Columns on the user row rather than a separate table.** `Language`, `Theme`
and `IsBlocked` already live on `ApplicationUser`. A one-to-one table holding
three strings and a timestamp would add a join without adding anything else.

**API version in configuration.** Salesforce retires versions on a schedule.
Keeping it a setting means the version can be moved without a code change.

---

## 7. Known limitations

**Not a distributed transaction.** Salesforce is written first, the local
database second. Should the Salesforce call succeed and the subsequent
`SaveChangesAsync` fail, the record ids are lost and the following sync would
create a duplicate. The window is narrow. Closing it properly would mean
recording the intent before calling out and reconciling afterwards, which is
more machinery than this integration warrants.

**One direction only.** Changes made in Salesforce do not propagate back.
Two-way synchronisation would require Platform Events or an outbound message
calling a webhook on this side.

**Manual rather than automatic.** Editing a profile does not re-push to the CRM;
the user chooses when to sync. Automatic propagation would need a background
queue, because a CRM being slow or unavailable must not make saving a profile
fail.

**Deleting a user here does not delete them in Salesforce.** A CRM is normally
the system of record for customer relationships and should not lose history
because an account was closed. The two can therefore drift.

**City and country are free text.** Correct against a stock org. If State and
Country Picklists are enabled, the values must match the org's list exactly; the
resulting error is explained rather than shown raw, but the sync will not
succeed until the value is corrected.

**The token cache is per instance.** With several instances running, each
maintains its own token. This costs a few extra token requests and nothing else.
