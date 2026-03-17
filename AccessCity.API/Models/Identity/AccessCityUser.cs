using Microsoft.AspNetCore.Identity;

namespace AccessCity.API.Models.Identity
{
    public class AccessCityUser : IdentityUser
    {
        public string? FullName { get; set; }
        public List<string> PreferredRoutes { get; set; } = new();
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        
        public virtual ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
    }
}
