namespace Palantir.Domain.Entities;

public class Organization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<User> Users { get; set; } = new List<User>();
    public ICollection<Team> Teams { get; set; } = new List<Team>();
    public ICollection<Customer> Customers { get; set; } = new List<Customer>();
    public ICollection<Project> Projects { get; set; } = new List<Project>();
    public ICollection<Conversation> Conversations { get; set; } = new List<Conversation>();
}
