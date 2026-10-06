# Salesforce CRM Integration

The profile page has a "Sync to Salesforce" action. It opens a form, collects a
few details the application does not otherwise store, and creates an **Account**
(the company) with a **Contact** linked to it (the person) in a Salesforce org
through the REST API.

Pressing it a second time updates those same two records instead of creating
duplicates.

---

## 1. Every file added or changed

### Added

| File | What it does |
|---|---|
| `CvManager/Services/SalesforceOptions.cs` | The settings class: login URL, client id, client secret, API version, token cache window. Bound from the `Salesforce` configuration section, so the real values come from config and never from code. Also exposes `IsConfigured`, which the page uses to explain itself when no credentials are set. |
| `CvManager/Services/SalesforceService.cs` | **The only class that knows Salesforce exists.** Gets an access token, builds the Composite API request, sends it, reads the response, and turns every possible failure into a plain sentence. Also holds the token cache and the retry logic. |
| `CvManager/Models/ViewModels/SalesforceViewModels.cs` | The form's shape and its validation rules, plus the list of standard Salesforce Industry picklist values used to fill the dropdown. |
| `CvManager/Controllers/SalesforceController.cs` | Thin controller. Works out whose profile is being synced, checks the caller is allowed to, reads the non-removable profile fields, calls the service, and saves the returned record ids. No HTTP or JSON in here at all. |
| `CvManager/Views/Salesforce/Sync.cshtml` | The page: what will be sent from the profile (read-only), the extra-info form, and — after a first sync — the two record ids as links straight into the Salesforce org. |
| `CvManager/Migrations/20260930155746_SalesforceSync.cs` | Adds the ten new columns to `AspNetUsers`. Every one is nullable, so it cannot fail on existing rows and loses no data. |

### Changed

| File | What changed and why |
|---|---|
| `CvManager/Models/ApplicationUser.cs` | Ten new properties. Three are the integration's memory — `SalesforceAccountId`, `SalesforceContactId`, `SalesforceSyncedAt` — and are the entire reason a second sync updates rather than duplicates. The other seven (`CompanyName`, `Industry`, `Website`, `Phone`, `JobTitle`, `City`, `Country`) hold what was typed into the form so it pre-fills next time. They live here because `Language`, `Theme` and `IsBlocked` already do. |
| `CvManager/Data/ApplicationDbContext.cs` | A new `ConfigureUsers` method setting a maximum length on each new column. The lengths deliberately match the Salesforce fields they end up in, so a value that fits our column always fits theirs. |
| `CvManager/Program.cs` | Binds `SalesforceOptions` from configuration and registers `SalesforceService` as a typed `HttpClient` with a 30-second timeout. A typed client means the socket is pooled rather than a new connection per sync. |
| `CvManager/appsettings.json` | An empty `Salesforce` section, so the shape of the configuration is documented in the repo while the real values stay out of it. |
| `CvManager/Views/Profile/Index.cshtml` | The "Sync to Salesforce" button, in the Me tab under a "CRM" heading, wrapped in `@if (Model.CanEdit)`. |
| `CvManager/Resources/SharedResource.ru.resx` | 25 Russian translations for the new interface text, so the feature works in both languages like the rest of the site. |

Nothing was deleted, and no existing behaviour changed.

---

## 2. What happens when the button is pressed

### Step 1 — The button

It sits on the profile page inside `@if (Model.CanEdit)`. `CanEdit` is true only
for the profile owner (in any role) and for admins. A recruiter reading somebody
else's profile gets `CanEdit == false` and never sees it.

### Step 2 — The form loads (`GET /Salesforce/Sync`)

`SalesforceController.Sync` calls `ResolveTarget`, which decides whose profile
this is:

- no `ownerId`, or your own id, means you
- somebody else's id means you must be an admin, otherwise access is denied

This is the real enforcement. Hiding the button is only cosmetic — a request
typed straight into the address bar hits the same check.

The page then shows what the application already knows (name, email, location)
as read-only text, and offers the form for what it does not: company name,
industry, website, phone, job title, city, country. On a second visit every box
is pre-filled from the last sync.

### Step 3 — You submit (`POST /Salesforce/Sync`)

The controller re-runs `ResolveTarget` — the check happens again on the POST, not
just the GET — then validates the form. An empty company name never reaches
Salesforce.

It reads First Name and Last Name out of the attribute table. In this
application a name is not a column; it is an attribute value. These two are
**system attributes**, which the profile page refuses to delete, so every user is
guaranteed to have them. That is what "non-removable fields" means here.

### Step 4 — Getting an access token

`SalesforceService` posts to `{LoginUrl}/services/oauth2/token`:

```
grant_type=client_credentials
client_id=<Consumer Key>
client_secret=<Consumer Secret>
```

This is the **Client Credentials flow**: server-to-server, with no user being
redirected to a Salesforce login screen. Salesforce answers with an
`access_token` and the `instance_url` to send API calls to.

The token is cached in a static field for 30 minutes. It is static because the
service is registered as a typed `HttpClient`, which makes it transient — a new
object per injection — so an instance field would cache nothing at all. A
`SemaphoreSlim` stops two simultaneous syncs both fetching a token.

### Step 5 — One Composite API call

`POST {instance_url}/services/data/v62.0/composite/` with two sub-requests:

```json
{
  "allOrNone": true,
  "compositeRequest": [
    {
      "method": "POST",
      "url": "/services/data/v62.0/sobjects/Account",
      "referenceId": "newAccount",
      "body": { "Name": "Acme Corp.", "Industry": "Technology",
                "Phone": "...", "Website": "...",
                "BillingCity": "Krakow", "BillingCountry": "Poland" }
    },
    {
      "method": "POST",
      "url": "/services/data/v62.0/sobjects/Contact",
      "referenceId": "newContact",
      "body": { "LastName": "Chen", "FirstName": "Mary",
                "Email": "mary.chen@example.com", "Title": "Data Engineer",
                "Phone": "...", "MailingCity": "Krakow",
                "MailingCountry": "Poland",
                "AccountId": "@{newAccount.id}" }
    }
  ]
}
```

Two things make this the interesting part:

- **`"AccountId": "@{newAccount.id}"`** — the Account does not have an id yet.
  Salesforce runs the first sub-request, substitutes the real id into the second,
  then runs it. The link is made inside Salesforce, so the application never has
  to create the Account, read the id back, and then create the Contact.
- **`"allOrNone": true`** — either both records are written or neither is.
  Without it, a valid Account plus an invalid Contact would leave a company in
  the CRM with nobody attached to it.

On a **second** sync the stored ids exist, so each sub-request becomes a `PATCH`
to `/sobjects/Account/{id}` and `/sobjects/Contact/{id}`, and `AccountId` is sent
literally rather than as a reference.

### Step 6 — Reading the answer

The response has one entry per sub-request, each with its own status code. A
successful `POST` returns 201 and the new id; a successful `PATCH` returns 204
and no body, so the ids already in hand are carried through.

Three things can go wrong, and each is handled differently:

| Problem | What happens |
|---|---|
| The cached token was rejected (401) | Throw the token away, get a new one, send the request **once** more. If a fresh token is also refused, the credentials are wrong and retrying would not help. |
| The stored ids point at records somebody deleted in Salesforce by hand (`NOT_FOUND`, `ENTITY_IS_DELETED`) | Clear them and send the request again as a create. The user sees a normal success, not an error they can do nothing about. |
| Salesforce rejected the data (a required field, a picklist value, a duplicate rule) | The error is translated into a readable sentence and shown on the form, with everything the user typed still in place. |

Network failures, timeouts and unparseable responses are caught too. `SyncAsync`
never throws.

### Step 7 — Saving the result

On success the controller writes the two ids and the timestamp onto the user row,
along with the form values, and redirects with a message that **names the record
ids**:

> Salesforce sync complete. Account 001XX000003DHPn and Contact 003XX0000049VUn were created.

Those ids are what make the demo convincing: the same string is on screen in the
application and in Salesforce.

---

## 3. What to set up in Salesforce

Sign up for a free Developer org at https://developer.salesforce.com/signup if
you have not already.

### Step 1 — Find your My Domain URL

**Setup** (gear icon, top right) → in **Quick Find** type `My Domain` → **My Domain**.

Copy **Current My Domain URL**. It looks like:

```
https://your-org-dev-ed.develop.my.salesforce.com
```

This is what goes in `Salesforce__LoginUrl`. It must be this URL and **not**
`login.salesforce.com` — the Client Credentials flow is only served from the
org's own domain, and pointing it at the generic login host returns an
`invalid_client` error that looks exactly like a wrong secret.

### Step 2 — Create the Connected App

**Setup** → Quick Find `App Manager` → **App Manager** → **New Connected App**
(top right).

If the org offers a choice between a Connected App and an External Client App,
either works; the Connected App path below is the simpler one.

Fill in:

- **Connected App Name**: `CV Manager Integration`
- **API Name**: leave as filled in automatically
- **Contact Email**: your email

Then tick **Enable OAuth Settings** and set:

- **Callback URL**: `https://login.salesforce.com/services/oauth2/callback`
  (a required field even though this flow never uses it)
- **Selected OAuth Scopes**: move these across
  - *Manage user data via APIs (api)*
  - *Perform requests at any time (refresh_token, offline_access)*
- **Enable Client Credentials Flow**: tick it. **This is the one that matters**
  and it is off by default. A warning appears; accept it.

**Save**, then **Continue**. Salesforce needs between two and ten minutes before
the new app will accept a token request.

### Step 3 — Choose the Run As user

The Client Credentials flow has no human logging in, so Salesforce needs to know
which user the integration acts as.

**Setup** → **App Manager** → find your app → arrow at the right of the row →
**Manage** → **Edit Policies**.

Under **Client Credentials Flow**, set **Run As** to a user — yourself is fine in
a Developer org. **Save**.

If a token request later fails with a restricted-IP error, come back to this same
screen and set **IP Relaxation** to *Relax IP restrictions*.

### Step 4 — Copy the Consumer Key and Secret

**Setup** → **App Manager** → your app → arrow → **View** → under **API (Enable
OAuth Settings)** click **Manage Consumer Details**.

Salesforce sends a verification code to your email. Enter it, then copy:

- **Consumer Key** → `Salesforce__ClientId`
- **Consumer Secret** → `Salesforce__ClientSecret`

### Step 5 — Confirm the API version

Open this in a browser while signed in to the org:

```
https://your-org-dev-ed.develop.my.salesforce.com/services/data/
```

It lists every version the org supports. Put the newest `version` value in
`Salesforce__ApiVersion` (the default in this project is `v62.0`).

---

## 4. Environment variables

In .NET a double underscore separates configuration sections, so
`Salesforce:ClientId` is written `Salesforce__ClientId` as an environment
variable.

| Variable | Example | Required |
|---|---|---|
| `Salesforce__LoginUrl` | `https://your-org-dev-ed.develop.my.salesforce.com` | yes |
| `Salesforce__ClientId` | `3MVG9...` (Consumer Key) | yes |
| `Salesforce__ClientSecret` | `A1B2C3...` (Consumer Secret) | yes |
| `Salesforce__ApiVersion` | `v62.0` | no, defaults to `v62.0` |
| `Salesforce__TokenCacheMinutes` | `30` | no, defaults to 30 |

### Locally

Put them in `CvManager/appsettings.Development.json`, which is **already
git-ignored** (line 5 of `.gitignore`):

```json
{
  "Salesforce": {
    "LoginUrl": "https://your-org-dev-ed.develop.my.salesforce.com",
    "ClientId": "paste the Consumer Key",
    "ClientSecret": "paste the Consumer Secret",
    "ApiVersion": "v62.0"
  }
}
```

The tracked `appsettings.json` keeps the same section with empty strings, so the
shape is documented without the secrets.

### On Cloud Run

Set them one at a time to avoid trouble with commas inside values:

```
gcloud run services update cv-manager --region us-central1 \
  --update-env-vars Salesforce__LoginUrl=https://your-org-dev-ed.develop.my.salesforce.com

gcloud run services update cv-manager --region us-central1 \
  --update-env-vars Salesforce__ClientId=3MVG9...

gcloud run services update cv-manager --region us-central1 \
  --update-env-vars Salesforce__ClientSecret=A1B2C3...
```

---

## 5. Testing it locally

```
cd cv-manager
dotnet run --project CvManager
```

The migration applies itself at startup, so there is no separate database step.

1. Sign in and open **My profile**.
2. On the **Me** tab, under **CRM**, press **Sync to Salesforce**.
3. If no credentials are configured the form appears disabled with an
   explanation — that is the expected behaviour, not a bug.
4. With credentials configured, fill the form in and submit. The green bar at the
   top of the page names the two record ids.
5. In Salesforce, go to the **Accounts** tab. The new company is there; open it
   and the Contact is listed under **Contacts** on the record.
6. Press the button again, change the job title, and submit. The message now says
   *updated* rather than *created*, and Salesforce still has exactly one Account.

### Checks already run against this build

| Check | Result |
|---|---|
| Not signed in, `GET /Salesforce/Sync` | redirected to the login page |
| Signed in as a candidate, own profile | form renders, 32 industry options, own email shown |
| Candidate requesting another user's `ownerId` (GET and POST) | access denied |
| Admin requesting another user's `ownerId` | allowed, that user's email shown |
| Empty company name | rejected with a readable message, nothing sent |
| Valid submit with no credentials configured | readable message, no crash |
| Stored ids present | panel shows both ids, button reads "Update in Salesforce", fields pre-filled |

---

## 6. Demo video script

The task says to record the data entry and show the data arriving in Salesforce.
Have the application in one browser tab and Salesforce in another.

1. **Show the permission rule first.** Signed in as a candidate, open your own
   profile and point at the **Sync to Salesforce** button. Then paste
   `/Salesforce/Sync?ownerId=<another user's id>` into the address bar and show
   that it is refused. Say the sentence: *"the button being hidden is not the
   security — the controller checks it on every request."*
2. **Open the form.** Point out the top section: name, email and location are
   read-only because they come from the non-removable profile fields. Only the
   rest is typed.
3. **Type the data on camera.** Company name, industry, website, job title,
   phone, city, country. This is the "record data entry" the task asks for.
4. **Submit.** Read the success message aloud, including the Account id.
5. **Switch to Salesforce.** Accounts tab, open the new Account. Point at the
   **Contacts** related list showing the linked Contact. Open the Contact and
   show the email came from the application, not the form.
6. **Show the id matches.** Compare the id in the Salesforce URL with the one in
   the success message.
7. **Press the button again.** Change the job title, submit, and show the message
   says *updated*. Refresh Salesforce: still one Account, and the title changed.
   Say: *"the record ids are stored on the user, so a second sync is an update,
   not a duplicate."*
8. **Optional, if you want to show the error handling**: delete the Account in
   Salesforce, then sync again. The application notices the record is gone and
   creates a new one instead of showing an error.

---

## 7. Limitations and likely questions

Say these plainly; they are all deliberate choices rather than oversights.

**Why the Client Credentials flow, and what does it cost?**
It is server-to-server: there is no user at a browser to redirect, which is
right for a background push. The cost is that the application authenticates as
one fixed "Run As" user, so every record in Salesforce is owned by that user
rather than by the person who pressed the button. A per-user identity would need
each user to have their own Salesforce login and the authorisation code flow.

**Why the Composite API instead of two REST calls?**
One round trip, and `allOrNone: true` makes it atomic. Two separate calls could
create an Account and then fail on the Contact, leaving an orphan company record
in the CRM with no way to notice.

**What stops a duplicate if I press the button twice?**
The returned ids are stored on the user row. When they are present the service
sends `PATCH` instead of `POST`. Demonstrated in the video.

**What if somebody deletes the records in Salesforce?**
The `PATCH` fails with `NOT_FOUND` or `ENTITY_IS_DELETED`. The service detects
those specific codes, discards the stale ids and retries as a create.

**The honest gap: this is not a distributed transaction.**
Salesforce is written first, then our database. If Salesforce succeeds and the
following `SaveChangesAsync` fails, the record ids are lost and the *next* sync
would create a duplicate. The window is very small, and the fix would be to
write an intent row before calling out and reconcile afterwards, which is more
machinery than this task needs. Worth naming before somebody else does.

**Why is Industry a dropdown and not a text box?**
`Industry` is a restricted picklist on a stock Salesforce Account, so a
free-text value like "Software" is rejected with
`INVALID_OR_NULL_FOR_RESTRICTED_PICKLIST`. The dropdown offers exactly the 32
standard values, so the sync cannot fail for that reason. An org whose picklist
has been customised would need that list in `SalesforceViewModels.cs` adjusted.

**Country and City are free text.**
That is fine in a stock org. If **State and Country Picklists** are switched on
in Salesforce, the values have to match its list exactly. The error is caught and
explained rather than shown raw, but the sync will not succeed until the value is
corrected.

**It only goes one way.**
A change made in Salesforce does not come back to the application. Two-way sync
would need Salesforce Platform Events or an outbound message calling a webhook.

**It is manual, not automatic.**
Editing your profile does not re-push to Salesforce. The user presses the button.
Doing it automatically would mean a background queue, because a CRM being slow or
down must not make saving a profile fail.

**Deleting a user here does not delete them in Salesforce.**
Deliberate: a CRM is usually the system of record for customer relationships and
should not lose history because somebody closed their account. It does mean the
two can drift.

**The token cache is per instance.**
It is a static field, so if Cloud Run runs several instances each one caches its
own token. Harmless — it just means a few more token requests than strictly
necessary.

**`LastName` has a fallback.**
It is the only genuinely required field on a Salesforce Contact. A user who has
not filled in their name yet would otherwise be blocked by a CRM validation
error, so the service falls back to the first name, then to the part of the email
before the `@`, then to "Unknown".

**Empty fields are not sent at all.**
Sending an empty string would blank out a value an administrator had filled in by
hand in Salesforce, which is not what "sync my details" should mean.
