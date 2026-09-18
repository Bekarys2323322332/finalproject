using CvManager.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CvManager.Data;

// Inherits from IdentityDbContext so the Identity tables (users, roles, external
// logins) and my own tables live in one database and one migration history.
public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<AttributeCategory> AttributeCategories => Set<AttributeCategory>();
    public DbSet<LibraryAttribute> LibraryAttributes => Set<LibraryAttribute>();
    public DbSet<AttributeOption> AttributeOptions => Set<AttributeOption>();
    public DbSet<AttributeValue> AttributeValues => Set<AttributeValue>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<ProjectTag> ProjectTags => Set<ProjectTag>();
    public DbSet<Position> Positions => Set<Position>();
    public DbSet<PositionAttribute> PositionAttributes => Set<PositionAttribute>();
    public DbSet<PositionProjectTag> PositionProjectTags => Set<PositionProjectTag>();
    public DbSet<PositionAccessRule> PositionAccessRules => Set<PositionAccessRule>();
    public DbSet<Cv> Cvs => Set<Cv>();
    public DbSet<CvLike> CvLikes => Set<CvLike>();
    public DbSet<DiscussionPost> DiscussionPosts => Set<DiscussionPost>();

    // Ids of the four attributes that always exist. They are referenced from the
    // profile page and from the CV header, so they need to be known constants.
    public const int FirstNameAttributeId = 1;
    public const int LastNameAttributeId = 2;
    public const int LocationAttributeId = 3;
    public const int PhotoAttributeId = 4;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        ConfigureAttributes(builder);
        ConfigureProjects(builder);
        ConfigurePositions(builder);
        ConfigureCvs(builder);
        SeedLookups(builder);

        // Optimistic locking, applied to every entity that implements IVersioned.
        // Marking the column as a concurrency token makes EF add it to the WHERE
        // clause of UPDATE and DELETE: "WHERE id = 7 AND version = 3". If another
        // user saved first, the row no longer matches, zero rows are affected and
        // EF throws DbUpdateConcurrencyException, which the controllers turn into
        // a "somebody else changed this" response.
        // This loop runs last so that every entity type is already discovered.
        foreach (var entityType in builder.Model.GetEntityTypes()
                     .Where(t => typeof(IVersioned).IsAssignableFrom(t.ClrType)))
        {
            builder.Entity(entityType.ClrType)
                .Property(nameof(IVersioned.Version))
                .IsConcurrencyToken();
        }
    }

    private static void ConfigureAttributes(ModelBuilder builder)
    {
        builder.Entity<AttributeCategory>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(100).IsRequired();
            e.HasIndex(c => c.Name).IsUnique();
        });

        builder.Entity<LibraryAttribute>(e =>
        {
            e.Property(a => a.Name).HasMaxLength(200).IsRequired();
            e.Property(a => a.Description).HasMaxLength(1000);

            // The globally unique attribute name required by the spec. Two
            // recruiters can hit "save" at the same instant, so the guarantee
            // has to live in the database, not in a check inside the controller.
            e.HasIndex(a => a.Name).IsUnique();

            // Used by the library screen's category filter.
            e.HasIndex(a => a.CategoryId);

            e.HasOne(a => a.Category)
                .WithMany(c => c.Attributes)
                .HasForeignKey(a => a.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<AttributeOption>(e =>
        {
            e.Property(o => o.Value).HasMaxLength(200).IsRequired();

            e.HasOne(o => o.Attribute)
                .WithMany(a => a.Options)
                .HasForeignKey(o => o.AttributeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AttributeValue>(e =>
        {
            // One answer per user per attribute. This index is both the
            // constraint and the lookup path used when a profile or CV loads all
            // of a candidate's values at once.
            e.HasIndex(v => new { v.UserId, v.AttributeId }).IsUnique();

            e.HasOne(v => v.User)
                .WithMany(u => u.AttributeValues)
                .HasForeignKey(v => v.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(v => v.Attribute)
                .WithMany(a => a.Values)
                .HasForeignKey(v => v.AttributeId)
                .OnDelete(DeleteBehavior.Cascade);

            // If a recruiter deletes one option of a dropdown, I do not want to
            // delete the candidate's whole answer row - the reference is just
            // cleared and the CV then shows that attribute as empty.
            e.HasOne(v => v.ValueOption)
                .WithMany()
                .HasForeignKey(v => v.ValueOptionId)
                .OnDelete(DeleteBehavior.SetNull);

            // Numeric access rules compare against this column, so give it a
            // fixed precision rather than letting it default.
            e.Property(v => v.ValueNumber).HasPrecision(18, 4);

            e.HasGeneratedTsVectorColumn(v => v.SearchVector, "english", v => new { v.ValueString })
                .HasIndex(v => v.SearchVector)
                .HasMethod("GIN");
        });
    }

    private static void ConfigureProjects(ModelBuilder builder)
    {
        builder.Entity<Project>(e =>
        {
            e.Property(p => p.Name).HasMaxLength(300).IsRequired();

            e.HasOne(p => p.User)
                .WithMany(u => u.Projects)
                .HasForeignKey(p => p.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // The CV generator asks for "this user's projects, newest first",
            // so this is the index that query rides on.
            e.HasIndex(p => new { p.UserId, p.StartDate });

            e.HasGeneratedTsVectorColumn(p => p.SearchVector, "english",
                    p => new { p.Name, p.DescriptionMarkdown })
                .HasIndex(p => p.SearchVector)
                .HasMethod("GIN");
        });

        builder.Entity<Tag>(e =>
        {
            e.Property(t => t.Name).HasMaxLength(100).IsRequired();

            // Tags are created on first use from user input, so concurrent
            // inserts of the same name are likely; the unique index is what
            // actually keeps the tag list clean.
            e.HasIndex(t => t.Name).IsUnique();
        });

        builder.Entity<ProjectTag>(e =>
        {
            e.HasKey(pt => new { pt.ProjectId, pt.TagId });

            e.HasOne(pt => pt.Project)
                .WithMany(p => p.ProjectTags)
                .HasForeignKey(pt => pt.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(pt => pt.Tag)
                .WithMany(t => t.ProjectTags)
                .HasForeignKey(pt => pt.TagId)
                .OnDelete(DeleteBehavior.Cascade);

            // Reverse lookup: "which projects carry this tag", used by the CV
            // project filter and the tag cloud links.
            e.HasIndex(pt => pt.TagId);
        });
    }

    private static void ConfigurePositions(ModelBuilder builder)
    {
        builder.Entity<Position>(e =>
        {
            e.Property(p => p.Title).HasMaxLength(300).IsRequired();
            e.Property(p => p.ShortDescription).HasMaxLength(2000);
            e.Property(p => p.Company).HasMaxLength(200);

            // The main page shows the most recently touched positions.
            e.HasIndex(p => p.UpdatedAt);

            e.HasGeneratedTsVectorColumn(p => p.SearchVector, "english",
                    p => new { p.Title, p.ShortDescription, p.Company })
                .HasIndex(p => p.SearchVector)
                .HasMethod("GIN");
        });

        builder.Entity<PositionAttribute>(e =>
        {
            e.HasKey(pa => new { pa.PositionId, pa.AttributeId });

            e.HasOne(pa => pa.Position)
                .WithMany(p => p.PositionAttributes)
                .HasForeignKey(pa => pa.PositionId)
                .OnDelete(DeleteBehavior.Cascade);

            // Deleting an attribute from the library also removes it from every
            // template that used it. The database does this, so I never write a
            // loop that deletes children one by one.
            e.HasOne(pa => pa.Attribute)
                .WithMany()
                .HasForeignKey(pa => pa.AttributeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PositionProjectTag>(e =>
        {
            e.HasKey(pt => new { pt.PositionId, pt.TagId });

            e.HasOne(pt => pt.Position)
                .WithMany(p => p.ProjectTags)
                .HasForeignKey(pt => pt.PositionId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(pt => pt.Tag)
                .WithMany(t => t.PositionProjectTags)
                .HasForeignKey(pt => pt.TagId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PositionAccessRule>(e =>
        {
            e.Property(r => r.ValueString).HasMaxLength(500);
            e.Property(r => r.ValueNumber).HasPrecision(18, 4);

            e.HasOne(r => r.Position)
                .WithMany(p => p.AccessRules)
                .HasForeignKey(r => r.PositionId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(r => r.Attribute)
                .WithMany()
                .HasForeignKey(r => r.AttributeId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureCvs(ModelBuilder builder)
    {
        builder.Entity<Cv>(e =>
        {
            // "A candidate may have at most one CV per position."
            e.HasIndex(c => new { c.PositionId, c.UserId }).IsUnique();

            e.HasOne(c => c.Position)
                .WithMany(p => p.Cvs)
                .HasForeignKey(c => c.PositionId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(c => c.User)
                .WithMany(u => u.Cvs)
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Statistics on the main page count CVs created in the last 24h.
            e.HasIndex(c => c.CreatedAt);
        });

        builder.Entity<CvLike>(e =>
        {
            // One like per recruiter per CV.
            e.HasIndex(l => new { l.CvId, l.UserId }).IsUnique();

            e.HasOne(l => l.Cv)
                .WithMany(c => c.Likes)
                .HasForeignKey(l => l.CvId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(l => l.User)
                .WithMany()
                .HasForeignKey(l => l.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<DiscussionPost>(e =>
        {
            e.Property(p => p.BodyMarkdown).HasMaxLength(10000).IsRequired();

            e.HasOne(p => p.Position)
                .WithMany(p => p.DiscussionPosts)
                .HasForeignKey(p => p.PositionId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(p => p.Author)
                .WithMany()
                .HasForeignKey(p => p.AuthorId)
                .OnDelete(DeleteBehavior.Cascade);

            // The discussion polls for "posts after id X in this position", which
            // is exactly this index.
            e.HasIndex(p => new { p.PositionId, p.Id });
        });
    }

    private static void SeedLookups(ModelBuilder builder)
    {
        // Categories are a lookup table with no editing UI, as the spec asks.
        // Adding one later means one INSERT, no code change.
        builder.Entity<AttributeCategory>().HasData(
            new AttributeCategory { Id = 1, Name = "Personal Information" },
            new AttributeCategory { Id = 2, Name = "Certification" },
            new AttributeCategory { Id = 3, Name = "Domain Knowledge" },
            new AttributeCategory { Id = 4, Name = "Soft Skills" },
            new AttributeCategory { Id = 5, Name = "Languages" },
            new AttributeCategory { Id = 6, Name = "Technical Skills" });

        // The mandatory "Me" attributes. They are ordinary library attributes so
        // a recruiter can put them on a template, but IsSystem stops them being
        // deleted or retyped.
        builder.Entity<LibraryAttribute>().HasData(
            new LibraryAttribute
            {
                Id = FirstNameAttributeId, Name = "First Name", CategoryId = 1,
                Type = AttributeType.String, IsSystem = true, SortOrder = 1,
                Description = "Candidate's given name."
            },
            new LibraryAttribute
            {
                Id = LastNameAttributeId, Name = "Last Name", CategoryId = 1,
                Type = AttributeType.String, IsSystem = true, SortOrder = 2,
                Description = "Candidate's family name."
            },
            new LibraryAttribute
            {
                Id = LocationAttributeId, Name = "Location", CategoryId = 1,
                Type = AttributeType.String, IsSystem = true, SortOrder = 3,
                Description = "City and country the candidate is based in."
            },
            new LibraryAttribute
            {
                Id = PhotoAttributeId, Name = "Personal Photo", CategoryId = 1,
                Type = AttributeType.Image, IsSystem = true, SortOrder = 4,
                Description = "Profile picture, stored in Cloudinary."
            });
    }

    public override int SaveChanges()
    {
        BumpVersionsAndTimestamps();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        BumpVersionsAndTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    // Increments Version on anything that changed. EF still puts the *original*
    // version in the WHERE clause and the incremented one in SET, so one save
    // both checks and advances the version. Doing it here means no controller can
    // forget to do it.
    private void BumpVersionsAndTimestamps()
    {
        foreach (var entry in ChangeTracker.Entries<IVersioned>())
        {
            if (entry.State == EntityState.Modified)
            {
                entry.Entity.Version++;
            }
        }

        foreach (var entry in ChangeTracker.Entries<Position>())
        {
            if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = DateTime.UtcNow;
            }
        }

        foreach (var entry in ChangeTracker.Entries<Cv>())
        {
            if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = DateTime.UtcNow;
            }
        }
    }
}
