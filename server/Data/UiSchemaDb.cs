using Microsoft.EntityFrameworkCore;

namespace DynamicUi.Server.Data;

public class UiSchemaDb : DbContext
{
    public UiSchemaDb(DbContextOptions<UiSchemaDb> options) : base(options) { }

    public DbSet<SchemaVersion> SchemaVersions => Set<SchemaVersion>();
    public DbSet<PatchEntry> PatchHistory => Set<PatchEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<SchemaVersion>().HasKey(s => s.Version);
        b.Entity<SchemaVersion>().Property(s => s.Version).ValueGeneratedNever();
        b.Entity<PatchEntry>().HasKey(p => p.Id);
    }
}
