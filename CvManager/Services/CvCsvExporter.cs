using System.Globalization;
using CsvHelper;
using CvManager.Data;
using CvManager.Models;
using Microsoft.EntityFrameworkCore;

namespace CvManager.Services;

// Optional extra: every CV for one position as a single CSV, so a recruiter can
// sort and compare candidates in Excel.
//
// The columns are not fixed - they are whatever attributes that position asks
// for - so the file is written from a dictionary per row rather than from a
// typed class.
public class CvCsvExporter
{
    private readonly ApplicationDbContext _db;

    public CvCsvExporter(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<byte[]> ExportAsync(int positionId, bool includeDrafts)
    {
        var attributes = await _db.PositionAttributes
            .Where(pa => pa.PositionId == positionId)
            .OrderBy(pa => pa.SortOrder)
            .Select(pa => new { pa.AttributeId, pa.Attribute!.Name, pa.Attribute.Type })
            .ToListAsync();

        var cvQuery = _db.Cvs.Where(c => c.PositionId == positionId);
        if (!includeDrafts)
        {
            cvQuery = cvQuery.Where(c => c.State == CvState.Published);
        }

        var cvs = await cvQuery
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new
            {
                c.Id,
                c.UserId,
                Email = c.User!.Email,
                c.State,
                Likes = c.Likes.Count,
                c.UpdatedAt
            })
            .ToListAsync();

        var userIds = cvs.Select(c => c.UserId).ToList();
        var attributeIds = attributes.Select(a => a.AttributeId).ToList();

        // Every value for every candidate in one query, then grouped in memory -
        // the alternative would be a query per candidate.
        var values = await _db.AttributeValues
            .Where(v => userIds.Contains(v.UserId) && attributeIds.Contains(v.AttributeId))
            .Include(v => v.ValueOption)
            .AsNoTracking()
            .ToListAsync();

        var byUser = values
            .GroupBy(v => v.UserId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(v => v.AttributeId));

        // Names live in the attribute values like anything else.
        var names = await _db.AttributeValues
            .Where(v => userIds.Contains(v.UserId)
                        && (v.AttributeId == ApplicationDbContext.FirstNameAttributeId
                            || v.AttributeId == ApplicationDbContext.LastNameAttributeId))
            .Select(v => new { v.UserId, v.AttributeId, v.ValueString })
            .ToListAsync();

        using var buffer = new MemoryStream();
        using (var writer = new StreamWriter(buffer, leaveOpen: true))
        using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
        {
            csv.WriteField("First name");
            csv.WriteField("Last name");
            csv.WriteField("Email");
            csv.WriteField("State");
            csv.WriteField("Likes");
            csv.WriteField("Updated");

            foreach (var attribute in attributes)
            {
                csv.WriteField(attribute.Name);
            }

            await csv.NextRecordAsync();

            foreach (var cv in cvs)
            {
                var first = names.FirstOrDefault(n => n.UserId == cv.UserId
                    && n.AttributeId == ApplicationDbContext.FirstNameAttributeId)?.ValueString;
                var last = names.FirstOrDefault(n => n.UserId == cv.UserId
                    && n.AttributeId == ApplicationDbContext.LastNameAttributeId)?.ValueString;

                csv.WriteField(first ?? string.Empty);
                csv.WriteField(last ?? string.Empty);
                csv.WriteField(cv.Email ?? string.Empty);
                csv.WriteField(cv.State.ToString());
                csv.WriteField(cv.Likes);
                csv.WriteField(cv.UpdatedAt.ToString("yyyy-MM-dd HH:mm"));

                byUser.TryGetValue(cv.UserId, out var userValues);

                foreach (var attribute in attributes)
                {
                    AttributeValue? value = null;
                    userValues?.TryGetValue(attribute.AttributeId, out value);
                    csv.WriteField(Format(value, attribute.Type));
                }

                await csv.NextRecordAsync();
            }
        }

        return buffer.ToArray();
    }

    // One column holds one value whatever its type, so each type is flattened to
    // the plainest text that still sorts sensibly in a spreadsheet.
    private static string Format(AttributeValue? value, AttributeType type)
    {
        if (value is not { HasValue: true })
        {
            return string.Empty;
        }

        return type switch
        {
            AttributeType.Boolean => value.ValueBoolean == true ? "yes" : "no",
            AttributeType.Numeric => value.ValueNumber?.ToString(CultureInfo.InvariantCulture) ?? "",
            AttributeType.Date => value.ValueDate?.ToString("yyyy-MM-dd") ?? "",
            AttributeType.Period => value.ValueDate is null
                ? ""
                : $"{value.ValueDate:yyyy-MM-dd}..{(value.ValueDateEnd?.ToString("yyyy-MM-dd") ?? "present")}",
            AttributeType.Dropdown => value.ValueOption?.Value ?? "",
            _ => value.ValueString ?? ""
        };
    }
}
