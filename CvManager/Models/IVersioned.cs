namespace CvManager.Models;

// Everything that can be edited by more than one person at a time carries a
// version. ApplicationDbContext.SaveChangesAsync bumps it automatically, and the
// column is registered as a concurrency token so the UPDATE carries the old
// value in its WHERE clause.
public interface IVersioned
{
    int Version { get; set; }
}
