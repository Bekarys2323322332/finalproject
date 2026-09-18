using CvManager.Data;
using CvManager.Models;
using Microsoft.EntityFrameworkCore;

namespace CvManager.Services;

// Tags are typed by users and created the first time they are used, so this
// turns a list of typed-in strings into Tag rows.
public class TagService
{
    private readonly ApplicationDbContext _db;

    public TagService(ApplicationDbContext db)
    {
        _db = db;
    }

    // Lowercased and trimmed, so "Python", "python" and " Python " end up as one
    // tag instead of three.
    public static string Normalize(string name) => name.Trim().ToLowerInvariant();

    public async Task<List<Tag>> GetOrCreateAsync(IEnumerable<string> names)
    {
        var wanted = names
            .Select(Normalize)
            .Where(n => n.Length > 0)
            .Distinct()
            .ToList();

        if (wanted.Count == 0)
        {
            return [];
        }

        // One query for all of them. Looping "find or insert" per tag would be
        // exactly the query-inside-a-loop the spec forbids.
        var existing = await _db.Tags
            .Where(t => wanted.Contains(t.Name))
            .ToListAsync();

        var missing = wanted.Except(existing.Select(t => t.Name)).ToList();
        if (missing.Count == 0)
        {
            return existing;
        }

        var created = missing.Select(name => new Tag { Name = name }).ToList();
        _db.Tags.AddRange(created);

        try
        {
            await _db.SaveChangesAsync();
            existing.AddRange(created);
            return existing;
        }
        catch (DbUpdateException)
        {
            // Two people can save the same new tag at the same moment. The unique
            // index rejects the loser, so drop my inserts and read the rows the
            // winner created instead of trying to prevent the race up front.
            foreach (var tag in created)
            {
                _db.Entry(tag).State = EntityState.Detached;
            }

            return await _db.Tags
                .Where(t => wanted.Contains(t.Name))
                .ToListAsync();
        }
    }

    // Autocomplete for the tag input: prefix match, most used first.
    public async Task<List<string>> SuggestAsync(string prefix, int take = 10)
    {
        var normalized = Normalize(prefix ?? string.Empty);

        var query = _db.Tags.AsQueryable();
        if (normalized.Length > 0)
        {
            query = query.Where(t => t.Name.StartsWith(normalized));
        }

        return await query
            .OrderByDescending(t => t.ProjectTags.Count)
            .ThenBy(t => t.Name)
            .Take(take)
            .Select(t => t.Name)
            .ToListAsync();
    }
}
