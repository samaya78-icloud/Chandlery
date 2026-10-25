namespace Chandlery.Models;

public class Company
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? RegistrationNumber { get; set; }
    public string? Address { get; set; }
    public string ContactEmail { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
