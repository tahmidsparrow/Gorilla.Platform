using Gorilla.IAM.Data.Entities;
using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;

namespace Gorilla.IAM.Data;

/// <summary>
/// Owns <c>subjects</c>, <c>credentials</c>, <c>role_grants</c>,
/// <c>consumer_apps</c> and <c>outbox</c> (spec section 3.4's "what we still
/// own" list) alongside OpenIddict's own tables (applications, authorizations,
/// scopes, tokens), registered below via <c>UseOpenIddict()</c>. The table
/// names are deliberately distinct — <see cref="Entities.ConsumerApp"/> maps
/// to <c>consumer_apps</c>, never <c>applications</c>, which OpenIddict owns.
/// </summary>
public class IamDbContext(DbContextOptions<IamDbContext> options) : DbContext(options)
{
    public DbSet<Subject> Subjects => Set<Subject>();
    public DbSet<Credential> Credentials => Set<Credential>();
    public DbSet<RoleGrant> RoleGrants => Set<RoleGrant>();
    public DbSet<ConsumerApp> ConsumerApps => Set<ConsumerApp>();
    public DbSet<ConsumerAppRole> ConsumerAppRoles => Set<ConsumerAppRole>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Subject>(e =>
        {
            e.ToTable("subjects");
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.Email).IsUnique();
            e.Property(s => s.Email).HasMaxLength(255).IsRequired();
            e.Property(s => s.Name).HasMaxLength(150).IsRequired();
        });

        modelBuilder.Entity<Credential>(e =>
        {
            e.ToTable("credentials");
            e.HasKey(c => c.SubjectId);
            e.Property(c => c.Hash).HasMaxLength(255).IsRequired();
            e.HasOne(c => c.Subject)
                .WithOne(s => s.Credential)
                .HasForeignKey<Credential>(c => c.SubjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RoleGrant>(e =>
        {
            e.ToTable("role_grants");
            e.HasKey(g => g.Id);
            e.HasIndex(g => new { g.SubjectId, g.AppKey, g.Role }).IsUnique();
            e.Property(g => g.AppKey).HasMaxLength(50).IsRequired();
            e.Property(g => g.Role).HasMaxLength(100).IsRequired();
            e.HasOne(g => g.Subject)
                .WithMany(s => s.RoleGrants)
                .HasForeignKey(g => g.SubjectId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(g => g.App)
                .WithMany()
                .HasForeignKey(g => g.AppKey)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ConsumerApp>(e =>
        {
            e.ToTable("consumer_apps");
            e.HasKey(a => a.Key);
            e.Property(a => a.Key).HasMaxLength(50);
            e.Property(a => a.Name).HasMaxLength(150).IsRequired();
        });

        modelBuilder.Entity<ConsumerAppRole>(e =>
        {
            e.ToTable("consumer_app_roles");
            e.HasKey(r => r.Id);
            e.HasIndex(r => new { r.AppKey, r.Role }).IsUnique();
            e.Property(r => r.Role).HasMaxLength(100).IsRequired();
            e.HasOne(r => r.App)
                .WithMany(a => a.Roles)
                .HasForeignKey(r => r.AppKey)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox");
            e.HasKey(m => m.Id);
            e.Property(m => m.Type).HasMaxLength(100).IsRequired();
            e.Property(m => m.Payload).HasColumnType("json").IsRequired();
            e.HasIndex(m => m.ProcessedAt);
        });

        // OpenIddict's own tables (applications, authorizations, scopes, tokens) —
        // separate from consumer_apps by design; see the class-level doc comment.
        modelBuilder.UseOpenIddict();

        // OpenIddict's default Id/reference columns are varchar(255) — fine
        // under SQL Server's index-key budget (nvarchar counts 2 bytes/char),
        // not under MySQL's: utf8mb4 counts 4 bytes/char, so
        // IX_OpenIddictTokens_ApplicationId_Status_Subject_Type alone
        // ((255+50+255+50) * 4 = 2440... plus per-column overhead) exceeds
        // MySQL's 3072-byte max key length. Not a hypothetical — this failed
        // running `dotnet ef database update` against a real MySQL 9 server.
        // OpenIddict's own IDs are GUID strings (36 chars); 100 is a safety
        // margin, applied uniformly to primary keys and the columns that
        // reference or accompany them so every composite index OpenIddict
        // defines stays under the byte budget and FK/PK widths stay matched.
        modelBuilder.Entity<OpenIddictEntityFrameworkCoreApplication>(entity =>
        {
            entity.Property(a => a.Id).HasMaxLength(100);
        });

        modelBuilder.Entity<OpenIddictEntityFrameworkCoreAuthorization>(entity =>
        {
            entity.Property(a => a.Id).HasMaxLength(100);
            // ApplicationId is a shadow FK property behind the Application
            // navigation, not a direct member of this class — hence the
            // string-keyed Property<T>() overload rather than a lambda.
            entity.Property<string>("ApplicationId").HasMaxLength(100);
            entity.Property(a => a.Status).HasMaxLength(50);
            entity.Property(a => a.Subject).HasMaxLength(100);
            entity.Property(a => a.Type).HasMaxLength(50);
        });

        modelBuilder.Entity<OpenIddictEntityFrameworkCoreToken>(entity =>
        {
            entity.Property(t => t.Id).HasMaxLength(100);
            entity.Property<string>("ApplicationId").HasMaxLength(100);
            entity.Property<string>("AuthorizationId").HasMaxLength(100);
            entity.Property(t => t.Status).HasMaxLength(50);
            entity.Property(t => t.Subject).HasMaxLength(100);
            entity.Property(t => t.Type).HasMaxLength(50);
        });
    }
}
