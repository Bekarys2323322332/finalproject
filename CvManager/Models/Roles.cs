namespace CvManager.Models;

// Role names live in one place so a typo in a string cannot silently grant or
// deny access. Used by [Authorize(Roles = ...)] and by the seeder.
public static class Roles
{
    public const string Admin = "Admin";
    public const string Recruiter = "Recruiter";
    public const string Candidate = "Candidate";

    // Recruiters and admins both manage positions, attributes and read CVs, so
    // most of those actions carry this pair.
    public const string RecruiterOrAdmin = Recruiter + "," + Admin;

    public static readonly string[] All = [Admin, Recruiter, Candidate];
}
