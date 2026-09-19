using CvManager.Data;
using CvManager.Models;
using Microsoft.EntityFrameworkCore;

namespace CvManager.Services;

public record Badge(string Title, string Caption, bool Earned, int Progress, int Target);

// Optional extra: achievements shown on the profile as an SVG panel the user can
// download. The counts come from three aggregate queries - nothing is stored, a
// badge is simply derived from the data that already exists.
public class BadgeService
{
    private readonly ApplicationDbContext _db;

    public BadgeService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<List<Badge>> ForUserAsync(string userId)
    {
        var projects = await _db.Projects.CountAsync(p => p.UserId == userId);
        var publishedCvs = await _db.Cvs.CountAsync(c => c.UserId == userId && c.State == CvState.Published);

        // Likes the candidate received across all of their CVs.
        var likes = await _db.CvLikes.CountAsync(l => l.Cv!.UserId == userId);

        return
        [
            Make("First steps", "1 project added", projects, 1),
            Make("Busy builder", "10 projects added", projects, 10),
            Make("On the market", "1 CV published", publishedCvs, 1),
            Make("Five applications", "5 CVs published", publishedCvs, 5),
            Make("Noticed", "5 likes received", likes, 5),
            Make("Popular", "25 likes received", likes, 25)
        ];
    }

    private static Badge Make(string title, string caption, int progress, int target) =>
        new(title, caption, progress >= target, Math.Min(progress, target), target);

    // Hand-built SVG: it is a fixed grid of cards, so a charting library would be
    // more weight than the markup it replaces. Text is escaped because badge
    // captions end up inside the document.
    public static string RenderSvg(string userName, IReadOnlyList<Badge> badges)
    {
        const int cardWidth = 210;
        const int cardHeight = 70;
        const int columns = 3;
        const int gap = 12;
        const int paddingTop = 56;

        var rows = (int)Math.Ceiling(badges.Count / (double)columns);
        var width = columns * cardWidth + (columns + 1) * gap;
        var height = paddingTop + rows * (cardHeight + gap) + gap;

        var svg = new System.Text.StringBuilder();
        svg.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" font-family=\"Segoe UI, sans-serif\">");
        svg.Append($"<rect width=\"{width}\" height=\"{height}\" rx=\"12\" fill=\"#0d1117\"/>");
        svg.Append($"<text x=\"{gap}\" y=\"32\" fill=\"#e6edf3\" font-size=\"18\" font-weight=\"600\">{Escape(userName)}</text>");
        svg.Append($"<text x=\"{gap}\" y=\"48\" fill=\"#8b949e\" font-size=\"11\">CV Manager achievements</text>");

        for (var i = 0; i < badges.Count; i++)
        {
            var badge = badges[i];
            var column = i % columns;
            var row = i / columns;
            var x = gap + column * (cardWidth + gap);
            var y = paddingTop + row * (cardHeight + gap);

            var fill = badge.Earned ? "#1f6feb" : "#161b22";
            var stroke = badge.Earned ? "#388bfd" : "#30363d";
            var titleColour = badge.Earned ? "#ffffff" : "#8b949e";

            svg.Append($"<g transform=\"translate({x},{y})\">");
            svg.Append($"<rect width=\"{cardWidth}\" height=\"{cardHeight}\" rx=\"10\" fill=\"{fill}\" stroke=\"{stroke}\"/>");
            svg.Append($"<text x=\"14\" y=\"26\" fill=\"{titleColour}\" font-size=\"13\" font-weight=\"600\">{Escape(badge.Title)}</text>");
            svg.Append($"<text x=\"14\" y=\"44\" fill=\"#8b949e\" font-size=\"10\">{Escape(badge.Caption)}</text>");

            // Progress bar, so an unearned badge still shows how close it is.
            var barWidth = cardWidth - 28;
            var filled = badge.Target == 0 ? 0 : (int)(barWidth * (badge.Progress / (double)badge.Target));
            svg.Append($"<rect x=\"14\" y=\"52\" width=\"{barWidth}\" height=\"6\" rx=\"3\" fill=\"#30363d\"/>");
            svg.Append($"<rect x=\"14\" y=\"52\" width=\"{filled}\" height=\"6\" rx=\"3\" fill=\"{(badge.Earned ? "#3fb950" : "#58a6ff")}\"/>");
            svg.Append($"<text x=\"{cardWidth - 14}\" y=\"58\" text-anchor=\"end\" fill=\"#8b949e\" font-size=\"9\">{badge.Progress}/{badge.Target}</text>");
            svg.Append("</g>");
        }

        svg.Append("</svg>");
        return svg.ToString();
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
