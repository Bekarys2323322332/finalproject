using System.Globalization;
using CvManager.Data;
using CvManager.Filters;
using CvManager.Models;
using CvManager.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Cloud Run hands the port to the container in an environment variable.
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddDefaultIdentity<ApplicationUser>(options =>
    {
        // No mail server is wired up, so requiring a confirmed address would
        // lock everybody out. Email confirmation is one of the optional extras.
        options.SignIn.RequireConfirmedAccount = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredLength = 6;
        options.User.RequireUniqueEmail = true;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>();

// Social login. Both providers are only registered when their keys are present,
// so the app still starts locally before the keys exist.
var authentication = builder.Services.AddAuthentication();

var googleId = builder.Configuration["Authentication:Google:ClientId"];
var googleSecret = builder.Configuration["Authentication:Google:ClientSecret"];
if (!string.IsNullOrWhiteSpace(googleId) && !string.IsNullOrWhiteSpace(googleSecret))
{
    authentication.AddGoogle(options =>
    {
        options.ClientId = googleId;
        options.ClientSecret = googleSecret;
    });
}

var gitHubId = builder.Configuration["Authentication:GitHub:ClientId"];
var gitHubSecret = builder.Configuration["Authentication:GitHub:ClientSecret"];
if (!string.IsNullOrWhiteSpace(gitHubId) && !string.IsNullOrWhiteSpace(gitHubSecret))
{
    authentication.AddGitHub(options =>
    {
        options.ClientId = gitHubId;
        options.ClientSecret = gitHubSecret;
        // GitHub only returns the email when this scope is asked for, and
        // Identity needs an email to key the account on.
        options.Scope.Add("user:email");
    });
}

// Cloud Run terminates HTTPS at its front end and forwards plain HTTP to the
// container. Without this the app thinks every request is http, builds an
// http:// OAuth callback URL and Google refuses it with redirect_uri_mismatch.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // The proxy's address is not known ahead of time, so the default
    // "only trust localhost" restriction has to be lifted.
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

// The auto-save posts JSON, so it cannot put the antiforgery token in a form
// field - naming a header lets it send the token that way instead.
builder.Services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");

builder.Services.AddControllersWithViews(options =>
    {
        // Runs before every action and throws out users who were blocked or
        // deleted after they signed in.
        options.Filters.Add<ActiveUserFilter>();
    })
    .AddViewLocalization()
    .AddDataAnnotationsLocalization();

builder.Services.AddRazorPages();

// English plus Russian, remembered in a cookie. The cookie is written from the
// user's saved preference on sign-in, so the choice follows the account.
var supportedCultures = new[] { new CultureInfo("en"), new CultureInfo("ru") };
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture("en");
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;
});

// QuestPDF needs its licence mode set once before any document is produced.
// Community covers this project.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

builder.Services.AddScoped<PositionAccessService>();
builder.Services.AddScoped<CvBuilder>();
builder.Services.AddScoped<TagService>();
builder.Services.AddSingleton<MarkdownRenderer>();
builder.Services.AddScoped<CvPdfService>();
builder.Services.AddScoped<BadgeService>();
builder.Services.AddScoped<CvCsvExporter>();

var app = builder.Build();

// Apply migrations and create the roles (and the first admin) on startup, so a
// fresh deployment needs no manual database step.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await db.Database.MigrateAsync();
    await DbSeeder.SeedAsync(scope.ServiceProvider, app.Configuration);
}

// First in the pipeline, so every later component sees the original scheme.
app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();

app.UseRequestLocalization();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapRazorPages();

app.Run();
