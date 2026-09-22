# File-by-file guide

Every source file in the project, what it does and why it exists.
Read `ARCHITECTURE.md` first for the ideas; this is the map.

---

## Startup and configuration

### `Program.cs`
The only composition root. In order:
1. reads `PORT` (Cloud Run supplies it) and binds Kestrel to it;
2. registers `ApplicationDbContext` on Npgsql;
3. registers Identity with roles, relaxed password rules and no e-mail
   confirmation requirement;
4. registers Google and GitHub **only if their keys are present**, so the app
   still starts locally without them;
5. configures forwarded headers — see below, this one matters;
6. sets the antiforgery header name, because the auto-save posts JSON and cannot
   put the token in a form field;
7. adds MVC with the global `ActiveUserFilter`, view localization, and the
   en/ru culture list;
8. registers the services;
9. runs migrations and the seeder at startup, so a fresh deployment needs no
   manual database step;
10. builds the pipeline: `UseForwardedHeaders` **first**, then static files,
    routing, localization, authentication, authorization.

`app.UseForwardedHeaders()` has to be first. Cloud Run terminates HTTPS at its
front end and hands the container plain HTTP, so without it the app thinks the
scheme is http, builds an `http://` OAuth callback, and Google refuses with
`redirect_uri_mismatch`.

### `appsettings.json` / `appsettings.Development.json`
Structure with empty values in the first; real local values in the second, which
is **git-ignored**. In production every value arrives as a Cloud Run environment
variable (`ConnectionStrings__DefaultConnection`, `Authentication__Google__…`).
No secret is ever committed.

### `Dockerfile`
Two stages: SDK image restores and publishes, runtime image carries only the
output. `EXPOSE 8080` matches the port Cloud Run expects.

### `SharedResource.cs`
An empty marker class. `IStringLocalizer<SharedResource>` only needs the type's
name to locate `Resources/SharedResource.*.resx`.

### `Resources/SharedResource.ru.resx`
The Russian translations. Keys are the **English strings themselves**, so a
missing translation degrades to readable English instead of a blank or a key
name. Only the UI is translated — user content never is.

---

## Domain model (`Models/`)

### `Enums.cs`
`AttributeType` (the eight types), `RuleOperator`, `CvState`, `PositionLevel`.
The attribute's type decides which value column is used and which operators the
rule editor may offer.

### `IVersioned.cs`
One property, `int Version`. Everything editable by more than one person
implements it; the DbContext finds them by this interface and registers the
column as a concurrency token.

### `ApplicationUser.cs`
Extends `IdentityUser` with only what Identity has no place for: `Language`,
`Theme`, `IsBlocked`, `CreatedAt`, and navigation collections.
`IsBlocked` is a plain bool rather than Identity's `LockoutEnd` because blocking
here is permanent until an admin lifts it, and a bool is far easier to filter on.

### `Roles.cs`
Role-name constants plus `RecruiterOrAdmin`. Exists so a typo in a magic string
cannot silently grant or deny access.

### `Attributes.cs`
Four types:
- `AttributeCategory` — lookup only, used for grouping/filtering. No edit UI by
  design; a new category is one INSERT.
- `LibraryAttribute` — name (globally unique), category, description, type,
  `IsSystem`, `SortOrder`, `LastUsedAt`, `Version`, options.
- `AttributeOption` — one dropdown choice.
- `AttributeValue` — **the single master copy of one person's answer.** Typed
  columns (`ValueString`, `ValueNumber`, `ValueDate`, `ValueDateEnd`,
  `ValueBoolean`, `ValueOptionId`), plus `HasValue` and `SearchVector`.

`HasValue` exists because `0`, `false` and `""` are all real answers — emptiness
cannot be inferred from the columns. Dropdowns store the option **id**, so
renaming a choice does not invalidate everyone's stored answer.

### `Projects.cs`
`Project` (name, start, nullable end meaning "ongoing", markdown description,
version, search vector), `Tag` (lowercased, unique), `ProjectTag` (explicit join,
because the tag cloud and the project filter query it directly).

### `Positions.cs`
`Position` (title, description, company, level, `IsPublic`, `MaxProjects`,
timestamps, version, search vector) plus `PositionAttribute`,
`PositionProjectTag` and `PositionAccessRule`. The rule carries the same typed
value columns as `AttributeValue`, so comparisons happen like against like.

### `Cvs.cs`
`Cv` — position, user, state, timestamps, version, **and no content**.
`CvLike` — one row per recruiter per CV.
`DiscussionPost` — append-only message with markdown body.

### `ErrorViewModel.cs`
From the template; used by the error page.

---

## Data layer (`Data/`)

### `ApplicationDbContext.cs`
The single most important file after the services. It sets up:

- **Unique indexes**: `LibraryAttributes.Name`, `Tags.Name`,
  `(UserId, AttributeId)` on values, `(PositionId, UserId)` on CVs,
  `(CvId, UserId)` on likes. These are the real guarantees — not code checks.
- **Composite keys** for the join tables.
- **Cascade rules**, so deleting a position or a user removes its dependents in
  one database statement. The one exception is `AttributeValue.ValueOption`,
  which is `SetNull`: deleting a dropdown option should not delete somebody's
  whole answer row.
- **`tsvector` generated columns with GIN indexes** on positions, projects and
  attribute values.
- **Seed data**: six categories and the four built-in attributes with fixed ids.
- **Optimistic locking**: a loop marks `Version` as a concurrency token on every
  `IVersioned` type.
- **`SaveChanges` override** that increments `Version` and stamps `UpdatedAt`,
  so no controller can forget to.

### `DbSeeder.cs`
Creates the three roles at startup, and promotes the e-mail in
`Seed:AdminEmail` to Admin. That solves the bootstrap problem: only an admin can
grant Admin, so the first one has to come from configuration.

---

## Cross-cutting (`Filters/`)

### `ActiveUserFilter.cs`
Runs before every action. Two jobs:
1. reload the user and sign out anyone blocked or deleted since sign-in — their
   cookie would otherwise stay valid until it expired;
2. give a brand-new account the `Candidate` role and call `RefreshSignInAsync`,
   because the role lives inside the auth cookie.

It skips actions marked `[AllowAnonymous]`, otherwise a blocked user could not
even reach the login page and the redirect would loop.

---

## Services (`Services/`)

### `PositionAccessService.cs` — killer feature #2's brain
`AccessibleTo(userId)` returns `IQueryable<Position>` where the position is
public **or** every rule is satisfied. Written as one LINQ expression so it runs
as a single SQL statement with `EXISTS` sub-queries; returning `IQueryable` lets
callers add paging and projection. `OperatorsFor(type)` lives here too, beside
the evaluation, so the UI's options and the query's understanding cannot drift.

### `CvBuilder.cs` — killer feature #3's brain
`BuildAsync` assembles a CV: template attributes (one query), the candidate's
values for exactly those (one query, matched via dictionary), the built-in
header attributes, matching projects capped at `MaxProjects`, and like counts
done in the database. `EnsureAttributeRowsAsync` creates empty value rows when a
CV is created, which is how an attribute the candidate never filled in appears
on both the CV and their profile.

### `TagService.cs`
Normalises tags to lowercase, and resolves a whole list in **one** query instead
of a lookup per tag. If two people create the same tag simultaneously the unique
index rejects the loser, which is caught and re-read rather than prevented.

### `MarkdownRenderer.cs`
Wraps Markdig with one pipeline. `DisableHtml()` is the important line: users
write this text, so raw HTML must not pass through, or a project description
containing `<script>` would run in every recruiter's browser.

### `CvPdfService.cs` *(optional extra)*
QuestPDF lays the document out, QRCoder draws a code pointing at the live CV.
Uses the bundled **Lato** font — naming a system font like Calibri works on
Windows and fails in the Linux container.

### `CvCsvExporter.cs` *(optional extra)*
Every CV of one position as a spreadsheet. Columns are **dynamic** — one per
attribute the position asks for — so rows are written field by field rather than
from a typed class. All values load in one query and are grouped in memory.

### `BadgeService.cs` *(optional extra)*
Three aggregate counts (projects, published CVs, likes received) turned into
badges, drawn as SVG server-side with progress bars. Nothing is stored; a badge
is derived from data that already exists.

---

## Controllers

### `HomeController.cs`
The main page: latest positions, top five by CV count, tag cloud, statistics.
Every block is a projection or an aggregate — nothing loads whole entities it
will not show.

### `AttributesController.cs` — killer feature #1
Index with prefix search, category filter and paging; create/edit with a version
field; bulk delete that skips `IsSystem` rows **server-side**. `Save` catches
`PostgresException` `23505` from the unique index rather than pre-checking the
name. `SyncOptions` keeps unchanged option rows so candidates' answers survive.
`Suggest` and `Options` feed the picker and are open to any signed-in user —
candidates need them too — while every managing action carries the recruiter role.

### `PositionsController.cs` — killer feature #2
The biggest file, but each action is small and does one thing: `Index` (with
full-text search and an accessibility check for candidates), `Details` (tabs,
CV list, discussion), `Create`, `Edit`, `SaveBasics` (with optimistic locking and
a `[Bind(Prefix = "Basics")]` because the form is nested), `AddAttribute`,
`RemoveAttributes`, `AddRule` (parses the value into the right typed column),
`RemoveRules`, `SaveProjectTags`, `Duplicate`, `Delete`, `ExportCsv`, plus
`Messages`/`PostMessage` for the polled discussion.

### `CvsController.cs` — killer feature #3
`Create` (checks access server-side, one CV per position), `Details` (four
different permission outcomes), `Pdf`, `Publish` (re-checks completeness),
`Unpublish`, `Delete`, `ToggleLike`. The "lost access hides the CV" rule lives
in `Details`.

### `ProfileController.cs`
`Index`/`View`/`Edit` all funnel into `ShowProfile`, which builds the four
sections. Recruiters get `editable: false`; admins get `true` for anybody.
Also project CRUD and the badge endpoint. `TargetUser` is the guard that pins a
non-admin to their own id no matter what the form claims.

### `ValuesController.cs` — the locking showcase
The **only** endpoint that writes attribute values, which is what makes a value a
single master copy. Takes a batch with per-item versions, sets each as
`OriginalValue`, saves once; on conflict detaches the losers, saves the rest, and
returns the winning values marked `conflict: true`.

### `AdminController.cs`
User table with roles joined in the projection (one query, not `GetRolesAsync`
per user). Block/unblock use `ExecuteUpdateAsync` — a single UPDATE for the whole
selection. An admin removing their own Admin role triggers `RefreshSignInAsync`,
because the role sits in the cookie.

### `SearchController.cs`
Role-aware full-text search across positions, CVs and the user's own projects.
`EF.Functions.WebSearchToTsQuery` must appear **inside** each query expression —
hoisting it into a variable makes EF treat it as a client call and throw.

### `PreferencesController.cs`
Language and theme, written both to a cookie (immediate, works signed out) and
to the user row (follows the account). `SafeRedirect` only accepts local URLs, so
`returnUrl` cannot become an open redirect.

### `TagsController.cs`
One action feeding the tag autocomplete.

---

## View models (`Models/ViewModels/`)

Projections that keep queries narrow and views dumb:
`AttributeViewModels.cs` (index + edit form), `CvViewModels.cs`
(`CvAttributeRow` with its `DisplayText` formatting and `CvViewModel` with
`IsComplete`), `HomeViewModels.cs` (`PositionListItem`, `TagCloudItem`),
`PositionViewModels.cs` (`CvListItem`, edit/details models, discussion post),
`ProfileViewModels.cs` (profile sections, `ProfileCvItem.Hidden`, project form),
`SearchViewModel.cs`, and `ValueDtos.cs` (the auto-save request/response
contract, including `Conflict`).

---

## Views

### Shared
- `_Layout.cshtml` — navbar, the header search present on every page, theme and
  language switchers, status/error alerts. `data-bs-theme` on `<html>` is all
  Bootstrap 5.3 needs to switch the palette.
- `_LoginPartial.cshtml` — injects `SignInManager<ApplicationUser>`; the
  template's `IdentityUser` would not resolve.
- `_PositionTable.cshtml` — reusable read-only positions table.
- `_ValueField.cshtml` — renders one attribute value in **edit** or **read-only**
  mode, switching on type, and carries the `data-attribute-id` / `data-type` /
  `data-version` attributes the auto-save script reads.
- `_CloudinaryUploader.cshtml` — the upload widget when keys are configured, a
  URL prompt fallback when not.

### Pages
`Attributes/Index` + `Edit`, `Positions/Index` + `Edit` + `Details`,
`Cvs/Details`, `Profile/Index` + `Project`, `Search/Index`, `Admin/Users`,
`Home/Index`. Every table follows the same shape: a checkbox column, one toolbar
above, and no buttons in the rows.

---

## Client scripts (`wwwroot/js/`)

### `table-toolbar.js`
Keeps the select-all box in sync (including the indeterminate state) and enables
or disables toolbar buttons from the selection. `data-requires="one"` for Edit
and Duplicate, `"any"` for Delete. Clicking a row toggles its checkbox.

### `value-editor.js`
The auto-save. Tracks dirty fields locally, flushes every **5 seconds** (never
per keystroke), clears the queue before sending so typing during the request is
not lost, restores the batch on failure, and flushes on `beforeunload`. On a
conflict it writes the winning value into the field and says so.

### `site.css`
Only what Bootstrap does not cover: the red `value-missing` styling, the amber
`value-conflict` outline, and the photo drop zone.
