using CvManager.Models;
using Microsoft.AspNetCore.Identity;

namespace CvManager.Data;

// Runs once at startup. Categories and the built-in attributes come from
// HasData in the migration; the roles cannot, because Identity creates its own
// tables, so they are created here.
public static class DbSeeder
{
    public static async Task SeedAsync(IServiceProvider services, IConfiguration configuration)
    {
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();

        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new IdentityRole(role));
            }
        }

        // Bootstrapping problem: only an admin can hand out the Admin role, so
        // the very first one has to come from configuration. The email is set as
        // an environment variable on the server; whoever registers with it is
        // promoted on the next start.
        var adminEmail = configuration["Seed:AdminEmail"];
        if (string.IsNullOrWhiteSpace(adminEmail))
        {
            return;
        }

        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var admin = await userManager.FindByEmailAsync(adminEmail);

        if (admin is not null && !await userManager.IsInRoleAsync(admin, Roles.Admin))
        {
            await userManager.AddToRoleAsync(admin, Roles.Admin);
        }
    }
}
