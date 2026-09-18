using CvManager.Data;
using CvManager.Models;
using CvManager.Models.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace CvManager.Services;

// Builds a CV out of the pieces it is made of: the position decides which
// attributes and which project tags, the candidate supplies the values and the
// projects. Nothing here is cached or copied into the Cv row, so a CV always
// shows the candidate's current data.
public class CvBuilder
{
    private readonly ApplicationDbContext _db;

    public CvBuilder(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<CvViewModel?> BuildAsync(int cvId, string? viewerId, bool canEdit)
    {
        var cv = await _db.Cvs
            .Include(c => c.User)
            .Include(c => c.Position)
            .FirstOrDefaultAsync(c => c.Id == cvId);

        if (cv?.Position is null || cv.User is null)
        {
            return null;
        }

        // 1. What the position asks for, in the recruiter's order. Options are
        //    included because dropdown rows need them to render the <select>.
        var attributes = await _db.PositionAttributes
            .Where(pa => pa.PositionId == cv.PositionId)
            .OrderBy(pa => pa.SortOrder)
            .Select(pa => pa.Attribute!)
            .Include(a => a.Options.OrderBy(o => o.SortOrder))
            .AsNoTracking()
            .ToListAsync();

        var attributeIds = attributes.Select(a => a.Id).ToList();

        // 2. The candidate's answers for exactly those attributes - one query,
        //    then matched up in memory. Asking per attribute would be a query
        //    inside a loop.
        var values = await _db.AttributeValues
            .Where(v => v.UserId == cv.UserId && attributeIds.Contains(v.AttributeId))
            .Include(v => v.ValueOption)
            .AsNoTracking()
            .ToDictionaryAsync(v => v.AttributeId);

        var rows = attributes
            .Select(a => new CvAttributeRow
            {
                Attribute = a,
                Value = values.TryGetValue(a.Id, out var value) ? value : null
            })
            .ToList();

        var projects = await LoadProjectsAsync(cv.PositionId, cv.UserId, cv.Position.MaxProjects);

        // 3. Likes. Counted in the database rather than by loading the rows.
        var likeCount = await _db.CvLikes.CountAsync(l => l.CvId == cv.Id);
        var likedByMe = viewerId is not null
            && await _db.CvLikes.AnyAsync(l => l.CvId == cv.Id && l.UserId == viewerId);

        return new CvViewModel
        {
            Cv = cv,
            Position = cv.Position,
            Candidate = cv.User,
            Attributes = rows,
            Projects = projects,
            LikeCount = likeCount,
            LikedByCurrentUser = likedByMe,
            CanEdit = canEdit
        };
    }

    // The position's tags decide which projects belong in the CV; the newest
    // MaxProjects of the matching ones are taken. A position with no tags simply
    // takes the candidate's most recent projects.
    private async Task<List<Project>> LoadProjectsAsync(int positionId, string userId, int maxProjects)
    {
        var tagIds = await _db.PositionProjectTags
            .Where(pt => pt.PositionId == positionId)
            .Select(pt => pt.TagId)
            .ToListAsync();

        var query = _db.Projects
            .Where(p => p.UserId == userId);

        if (tagIds.Count > 0)
        {
            // "carries at least one of the wanted tags" - an EXISTS in SQL.
            query = query.Where(p => p.ProjectTags.Any(pt => tagIds.Contains(pt.TagId)));
        }

        return await query
            .OrderByDescending(p => p.StartDate)
            .Take(maxProjects)
            .Include(p => p.ProjectTags)
            .ThenInclude(pt => pt.Tag)
            .AsNoTracking()
            .ToListAsync();
    }

    // Creating a CV also makes sure the candidate has a row for every attribute
    // the position asks for - empty rows, so the CV can show them as missing and
    // so they turn up on the candidate's profile page ("attributes not present in
    // profile are empty by default").
    public async Task EnsureAttributeRowsAsync(int positionId, string userId)
    {
        var needed = await _db.PositionAttributes
            .Where(pa => pa.PositionId == positionId)
            .Select(pa => pa.AttributeId)
            .ToListAsync();

        if (needed.Count == 0)
        {
            return;
        }

        var existing = await _db.AttributeValues
            .Where(v => v.UserId == userId && needed.Contains(v.AttributeId))
            .Select(v => v.AttributeId)
            .ToListAsync();

        var missing = needed.Except(existing).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        _db.AttributeValues.AddRange(missing.Select(id => new AttributeValue
        {
            UserId = userId,
            AttributeId = id,
            HasValue = false
        }));

        await _db.SaveChangesAsync();
    }
}
