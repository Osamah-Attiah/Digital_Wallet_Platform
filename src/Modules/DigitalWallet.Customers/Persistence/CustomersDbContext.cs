using DigitalWallet.Customers.Domain;
using Microsoft.EntityFrameworkCore;

namespace DigitalWallet.Customers.Persistence;

public sealed class CustomersDbContext(DbContextOptions<CustomersDbContext> options) : DbContext(options)
{
    public const string Schema = "customers";
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CustomerStatusHistory> CustomerStatusHistory => Set<CustomerStatusHistory>();
    public DbSet<KycStatusHistory> KycStatusHistory => Set<KycStatusHistory>();
    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema(Schema);
        builder.Entity<Customer>(b => { b.ToTable("customers"); b.HasKey(x => x.Id); b.HasIndex(x => x.IdentityUserId).IsUnique(); b.HasIndex(x => new { x.CreatedAt, x.Id }); b.HasIndex(x => new { x.Status, x.CreatedAt, x.Id }); b.HasIndex(x => new { x.KycStatus, x.CreatedAt, x.Id }); b.Property(x => x.FirstName).HasMaxLength(100).IsRequired(); b.Property(x => x.LastName).HasMaxLength(100).IsRequired(); b.Property(x => x.PhoneNumber).HasMaxLength(32); b.Property(x => x.PreferredLanguage).HasMaxLength(16).IsRequired(); b.Property(x => x.Status).HasConversion<string>().HasMaxLength(32); b.Property(x => x.KycStatus).HasConversion<string>().HasMaxLength(32); b.Property(x => x.ConcurrencyVersion).IsConcurrencyToken(); });
        builder.Entity<CustomerStatusHistory>(b => { b.ToTable("customer_status_history"); b.HasKey(x => x.Id); b.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(32); b.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(32); b.Property(x => x.Reason).HasMaxLength(500); b.HasIndex(x => new { x.CustomerId, x.OccurredAt }); });
        builder.Entity<KycStatusHistory>(b => { b.ToTable("kyc_status_history"); b.HasKey(x => x.Id); b.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(32); b.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(32); b.Property(x => x.Reason).HasMaxLength(500); b.HasIndex(x => new { x.CustomerId, x.OccurredAt }); });
    }
}
