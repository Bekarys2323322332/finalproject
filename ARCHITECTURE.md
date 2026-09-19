# CV Manager — how it works

A recruitment platform where recruiters define **positions** out of reusable
**attributes**, and candidates generate **CVs** from them.

Stack: ASP.NET Core MVC (.NET 10), EF Core + Npgsql, PostgreSQL (Neon),
ASP.NET Core Identity, Bootstrap 5. Deployed to Google Cloud Run.

---

## 1. The data model, and the one idea behind it

Everything rests on a single decision: **an attribute value is stored once, per
user**, and nothing ever copies it.

```
AspNetUsers ──< AttributeValues >── LibraryAttributes ──< AttributeOptions
                                          │
                                          ├──< PositionAttributes >── Positions
                                          └──< PositionAccessRules >──┘
                                                                      │
AspNetUsers ──< Projects >── ProjectTags >── Tags ──< PositionProjectTags
                                                                      │
AspNetUsers ──< Cvs >─────────────────────────────────────────────────┘
                 └──< CvLikes
Positions ──< DiscussionPosts
```

`AttributeValues` has a unique index on `(UserId, AttributeId)`: one answer per
person per attribute. That is why editing "English Level" inside a CV changes it
on the profile and in every other CV at the same time — they are all reading the
same row.

### Why `AttributeValue` has many typed columns

`ValueString`, `ValueNumber`, `ValueDate`, `ValueDateEnd`, `ValueBoolean`,
`ValueOptionId`. One generic `text` column would have been shorter, but access
rules compare values in SQL — `GPA > 3.5` has to be a real numeric comparison on
an indexable column, not a cast of a string. The attribute's `Type` decides which
column is used; `ValuesController.Apply` clears the others so a value can never
be half numeric and half text.

`HasValue` is a separate flag because `0`, `false` and `""` are all legitimate
answers — emptiness cannot be inferred from the columns.

### Why a dropdown answer stores an option id

`ValueOptionId` points at `AttributeOptions`. Storing the text would go stale the
moment a recruiter renames a choice. Deleting an option sets the reference to
null (`DeleteBehavior.SetNull`) rather than deleting the candidate's whole row.

---

## 2. Killer feature #1 — the attribute library

`Controllers/AttributesController.cs`, `Models/Attributes.cs`

- One shared pool. No owner column, because any recruiter may edit anything.
- `Name` has a **unique index**. Registration of a duplicate is *not* prevented
  by a check-then-insert — two recruiters saving at the same instant would both
  pass such a check. Instead the insert is attempted and `PostgresException`
  with `SqlState == "23505"` is caught and turned into a field error.
- The four built-in attributes (First/Last Name, Location, Photo) are ordinary
  library rows with `IsSystem = true`. They can go on a position template like
  anything else, but delete skips them **server-side**, not just in the UI.
- The picker (`Suggest`) supports prefix lookup (`ILike(name, q + "%")` so the
  index is usable), category filter, and recently-used first via `LastUsedAt`.

---

## 3. Killer feature #2 — positions and access rules

`Controllers/PositionsController.cs`, `Services/PositionAccessService.cs`

A position is a template: chosen attributes, project tags, a max project count,
and either `IsPublic` or a set of rules.

`AccessibleTo(userId)` returns an `IQueryable<Position>`:

```csharp
_db.Positions.Where(p =>
    p.IsPublic ||
    p.AccessRules.All(rule => answered.Any(v =>
        v.AttributeId == rule.AttributeId && ( ...operator comparisons... ))));
```

Points worth knowing:

- It is **one SQL statement** (`All` becomes `NOT EXISTS (… NOT …)`). Evaluating
  rules in C# would mean loading every position and every value first.
- It returns `IQueryable`, so callers add their own paging and projection and
  only the rows actually shown are fetched.
- `answered` is filtered to `HasValue`, so an unanswered attribute satisfies no
  rule — including negative ones. "Remote Work is not checked" means the
  candidate said no, not that they never filled it in.
- `OperatorsFor(type)` sits next to the evaluation so the operators offered by
  the UI and the ones understood by the query cannot drift apart.

Duplicating a position copies the template (attributes, rules, tags) but not the
CVs or the discussion — those belong to the original.

---

## 4. Killer feature #3 — generated CVs

`Services/CvBuilder.cs`, `Controllers/CvsController.cs`

The `Cvs` row holds `PositionId`, `UserId`, `State`, timestamps, `Version` —
**and no content at all.** Everything shown is assembled per request:

1. the attributes the position asks for, in the recruiter's order;
2. the candidate's answers for exactly those attributes (one query, matched in
   memory — never one query per attribute);
3. the built-in profile attributes, always, for the header;
4. projects carrying at least one of the position's tags, newest first, cut to
   `MaxProjects` (a position with no tags simply takes the most recent).

Consequences that follow from this design, rather than from extra code:

- changing a profile value updates every CV instantly;
- losing access **hides** a CV instead of destroying it — the row is untouched,
  only the UI refuses to show it;
- a `Publish` is only allowed when every requested attribute has a value, and
  that is re-checked server-side even though the button is disabled.

---

## 5. Optimistic locking

`Models/IVersioned.cs`, `Data/ApplicationDbContext.cs`, `Controllers/ValuesController.cs`

Every editable entity carries an `int Version` registered as a **concurrency
token**:

```csharp
builder.Entity(type).Property("Version").IsConcurrencyToken();
```

EF then writes `UPDATE … WHERE Id = @id AND Version = @oldVersion`. If someone
else saved first, zero rows match and `DbUpdateConcurrencyException` is thrown.
`SaveChanges` is overridden to increment `Version` automatically, so no
controller can forget.

The page renders the version it read; the save sends it back; the server puts it
into `OriginalValue`:

```csharp
_db.Entry(row).Property(v => v.Version).OriginalValue = item.Version;
```

`ValuesController.Save` is the interesting case, because a batch may contain
both winners and losers:

1. try one `SaveChangesAsync` for the whole batch;
2. on conflict, EF hands back the offending entries — detach them, drop them
   from the batch, and save the rest in a second statement, so the user does not
   lose the other fields they just typed;
3. re-read what actually won and return it, marked `conflict: true`.

The client (`wwwroot/js/value-editor.js`) replaces the field with the winning
value and says so, instead of silently overwriting somebody else's edit.

**Why optimistic and not pessimistic:** locking a row on open would need a
write on every open, a forced-unlock path for abandoned locks, and would make
in-place editing impractical. Conflicts here are rare, so it is cheaper to
detect them at write time than to prevent them.

---

## 6. Auto-save

`wwwroot/js/value-editor.js`

- Changes are tracked locally in a `Set` of dirty attribute ids.
- A timer flushes every **5 seconds** — never per keystroke.
- The queue is cleared *before* the request goes out, so anything typed during
  the flight is caught by the next round.
- A failed request puts the batch back rather than dropping it.
- `beforeunload` flushes, so the last seconds of typing survive navigation.
- It posts JSON, so the antiforgery token travels in a header
  (`options.HeaderName = "RequestVerificationToken"` in `Program.cs`).

---

## 7. Roles and access

| | Anonymous | Candidate | Recruiter | Admin |
|---|---|---|---|---|
| Browse positions | ✔ read-only | ✔ | ✔ | ✔ |
| Statistics | ✔ | ✔ | ✔ | ✔ |
| Own profile / projects | — | ✔ | — | ✔ (any) |
| Create CV | — | ✔ if rules pass | — | ✔ |
| Read CVs | — | own only | published only | all |
| Attributes / positions | — | — | ✔ | ✔ |
| Like a CV | — | — | ✔ | ✔ |
| Manage users | — | — | — | ✔ |

- `Filters/ActiveUserFilter.cs` runs before every action: it reloads the user and
  signs out anyone blocked or deleted since sign-in, because the cookie would
  otherwise stay valid. It also assigns `Candidate` to a brand-new account and
  calls `RefreshSignInAsync`, since the role lives in the cookie.
- An admin can remove their own Admin role; the cookie is reissued immediately.

---

## 8. Performance rules the brief asked for

- **No `SELECT *`** — list screens project into view models
  (`PositionListItem`, `CvListItem`) so only the displayed columns are fetched.
- **No queries in loops** — values, tags and roles are loaded in one query and
  matched in memory. `TagService.GetOrCreateAsync` resolves a whole tag list in
  one round trip.
- **Cascade deletes are declared in the DbContext** and executed by Postgres.
  Deleting a position removes its CVs, rules, template rows and discussion in one
  statement; there is no hand-written child-deletion loop.
- **Full-text search** uses generated `tsvector` columns with **GIN** indexes on
  positions, projects and attribute values, queried with
  `websearch_to_tsquery` — not `LIKE '%term%'`.
  Note `EF.Functions.WebSearchToTsQuery` must appear *inside* the query
  expression; hoisting it into a variable makes EF treat it as a client call.

---

## 9. Things that only show up in deployment

- **Cloud Run terminates TLS at its proxy** and hands the container plain HTTP.
  Without `app.UseForwardedHeaders()` (first in the pipeline) the app builds
  `http://` OAuth callbacks and Google rejects them with `redirect_uri_mismatch`.
- **Images never touch this server.** The Cloudinary widget uploads straight from
  the browser with an *unsigned* preset, and only the returned URL is stored — so
  no API secret is ever exposed and no blob goes into the database.
- **Fonts**: the PDF uses Lato, the font QuestPDF bundles. Naming a system font
  like Calibri works on Windows and then fails in the Linux container.

---

## 10. Optional extras

- **PDF + QR** (`Services/CvPdfService.cs`) — QuestPDF lays out the CV, QRCoder
  draws a code pointing at the live page, so a printed copy leads back to it.
- **CSV export** (`Services/CvCsvExporter.cs`) — every CV of a position with one
  column per attribute. Columns are dynamic, so rows are written field by field
  rather than from a typed class.
- **Achievement badges** (`Services/BadgeService.cs`) — derived from three
  aggregate counts, drawn as SVG server-side, downloadable.

Not implemented: email-confirmed registration and per-field validators
(length/regex/range).
