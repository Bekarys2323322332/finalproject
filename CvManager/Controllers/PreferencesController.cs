using CvManager.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;

namespace CvManager.Controllers;

// Language and theme. Both are stored twice on purpose: in a cookie, so the
// choice applies immediately and also works for visitors who are not signed in,
// and on the user row, so it follows the account to another browser. On sign-in
// the saved value is written back into the cookie.
public class PreferencesController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;

    public PreferencesController(UserManager<ApplicationUser> userManager)
    {
        _userManager = userManager;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetLanguage(string culture, string? returnUrl)
    {
        if (culture is not ("en" or "ru"))
        {
            culture = "en";
        }

        // The framework's own cookie name and format, so UseRequestLocalization
        // picks it up without any extra code.
        Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true });

        var user = await _userManager.GetUserAsync(User);
        if (user is not null)
        {
            user.Language = culture;
            await _userManager.UpdateAsync(user);
        }

        return SafeRedirect(returnUrl);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetTheme(string theme, string? returnUrl)
    {
        if (theme is not ("light" or "dark"))
        {
            theme = "light";
        }

        Response.Cookies.Append("theme", theme,
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true });

        var user = await _userManager.GetUserAsync(User);
        if (user is not null)
        {
            user.Theme = theme;
            await _userManager.UpdateAsync(user);
        }

        return SafeRedirect(returnUrl);
    }

    // Only local paths are accepted, otherwise the returnUrl parameter would be
    // an open redirect that could bounce users to another site.
    private IActionResult SafeRedirect(string? returnUrl)
    {
        return Url.IsLocalUrl(returnUrl)
            ? Redirect(returnUrl!)
            : RedirectToAction("Index", "Home");
    }
}
