using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AccessCity.API.Models.Identity
{
    public class RefreshToken
    {
        [Key]
        [JsonIgnore]
        public int Id { get; set; }

        public string Token { get; set; } = string.Empty;
        public string CreatedByIp { get; set; } = string.Empty;
        public DateTime Expires { get; set; }
        public DateTime Created { get; set; } = DateTime.UtcNow;
        public DateTime? Revoked { get; set; }
        public string? RevokedByIp { get; set; }
        public string? ReplacedByToken { get; set; }
        public string? ReasonRevoked { get; set; }

        public bool IsExpired => DateTime.UtcNow >= Expires;
        public bool IsRevoked => Revoked != null;
        public bool IsActive => !IsRevoked && !IsExpired;

        public string UserId { get; set; } = string.Empty;
        [JsonIgnore]
        public virtual AccessCityUser User { get; set; } = null!;
    }
}
