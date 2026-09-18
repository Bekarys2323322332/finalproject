using CvManager.Data;
using CvManager.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CvManager.Controllers;

// User management. Admins can block, unblock, delete and hand out or take away
// roles - including their own Admin role, which the spec explicitly allows.
[Authorize(Roles = Roles.Admin)]
public class AdminController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;

    public AdminController(ApplicationDbContext db, UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager)
    {
        _db = db;
        _userManager = userManager;
        _signInManager = signInManager;
    }

    public async Task<IActionResult> Users(string? q)
    {
        var query = _db.Users.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(u => EF.Functions.ILike(u.Email!, q + "%"));
        }

        // Roles are joined in the projection, so this is one query rather than a
        // GetRolesAsync call per user.
        var users = await query
            .OrderBy(u => u.Email)
            .Take(200)
            .Select(u => new AdminUserRow
            {
                Id = u.Id,
                Email = u.Email ?? "",
                IsBlocked = u.IsBlocked,
                CreatedAt = u.CreatedAt,
                CvCount = u.Cvs.Count,
                Roles = _db.UserRoles
                    .Where(ur => ur.UserId == u.Id)
                    .Join(_db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name!)
                    .ToList()
            })
            .ToListAsync();

        ViewData["Query"] = q;
        return View(users);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Block(string[] ids)
    {
        // ExecuteUpdate issues a single UPDATE for the whole selection instead of
        // loading each user and saving them one at a time.
        await _db.Users.Where(u => ids.Contains(u.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsBlocked, true));

        TempData["Status"] = $"Blocked {ids.Length} user(s).";
        return RedirectToAction(nameof(Users));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unblock(string[] ids)
    {
        await _db.Users.Where(u => ids.Contains(u.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsBlocked, false));

        TempData["Status"] = $"Unblocked {ids.Length} user(s).";
        return RedirectToAction(nameof(Users));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(string[] ids)
    {
        var users = await _db.Users.Where(u => ids.Contains(u.Id)).ToListAsync();
        _db.Users.RemoveRange(users);

        // Their values, projects, CVs and likes go with them through the cascade
        // rules in the database.
        await _db.SaveChangesAsync();

        TempData["Status"] = $"Deleted {users.Count} user(s).";
        return RedirectToAction(nameof(Users));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetRole(string[] ids, string role, bool grant)
    {
        if (!Roles.All.Contains(role))
        {
            return BadRequest();
        }

        var users = await _db.Users.Where(u => ids.Contains(u.Id)).ToListAsync();
        var me = _userManager.GetUserId(User);
        var removedOwnAdmin = false;

        foreach (var user in users)
        {
            // UserManager writes the join rows; there is no query in this loop,
            // only the role membership calls it needs.
            if (grant)
            {
                if (!await _userManager.IsInRoleAsync(user, role))
                {
                    await _userManager.AddToRoleAsync(user, role);
                }
            }
            else
            {
                if (await _userManager.IsInRoleAsync(user, role))
                {
                    await _userManager.RemoveFromRoleAsync(user, role);

                    if (user.Id == me && role == Roles.Admin)
                    {
                        removedOwnAdmin = true;
                    }
                }
            }
        }

        if (removedOwnAdmin)
        {
            // An admin may drop their own Admin role. The role sits in the auth
            // cookie, so it has to be reissued or they would keep admin rights
            // until the cookie expired.
            var self = await _userManager.FindByIdAsync(me!);
            if (self is not null)
            {
                await _signInManager.RefreshSignInAsync(self);
            }

            TempData["Status"] = "You removed your own Admin role.";
            return RedirectToAction("Index", "Home");
        }

        TempData["Status"] = grant
            ? $"Granted {role} to {users.Count} user(s)."
            : $"Removed {role} from {users.Count} user(s).";

        return RedirectToAction(nameof(Users));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult OpenProfile(string[] ids)
    {
        // Admins act as the owner of any personal page.
        return ids.Length == 1
            ? RedirectToAction("Edit", "Profile", new { userId = ids[0] })
            : RedirectToAction(nameof(Users));
    }
}

public class AdminUserRow
{
    public string Id { get; init; } = "";
    public string Email { get; init; } = "";
    public bool IsBlocked { get; init; }
    public DateTime CreatedAt { get; init; }
    public int CvCount { get; init; }
    public List<string> Roles { get; init; } = [];
}
