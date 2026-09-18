using CvManager.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CvManager.Filters;

// Runs before every action. Two jobs:
//   1. Throw out users who were blocked or deleted after they signed in - their
//      cookie is still valid, so without this check they would keep working
//      until it expired.
//   2. Give a brand new account the Candidate role, because registration goes
//      through the stock Identity pages which know nothing about my roles.
public class ActiveUserFilter : IAsyncActionFilter
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;

    public ActiveUserFilter(UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager)
    {
        _userManager = userManager;
        _signInManager = signInManager;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.HttpContext.User.Identity?.IsAuthenticated != true)
        {
            await next();
            return;
        }

        // Anonymous pages (sign-in, sign-out, the public position list) must not
        // be caught by this, otherwise a blocked user cannot even reach the
        // login page and the redirect loops.
        if (context.ActionDescriptor.EndpointMetadata.OfType<AllowAnonymousAttribute>().Any())
        {
            await next();
            return;
        }

        var user = await _userManager.GetUserAsync(context.HttpContext.User);

        if (user is null || user.IsBlocked)
        {
            await _signInManager.SignOutAsync();
            context.Result = new RedirectToPageResult("/Account/Login", new { area = "Identity" });
            return;
        }

        var roles = await _userManager.GetRolesAsync(user);
        if (roles.Count == 0)
        {
            await _userManager.AddToRoleAsync(user, Roles.Candidate);
            // The role is a claim inside the auth cookie, so the cookie has to be
            // reissued or the new role will not be visible until the next login.
            await _signInManager.RefreshSignInAsync(user);
        }

        await next();
    }
}
