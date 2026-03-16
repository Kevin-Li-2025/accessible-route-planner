using Microsoft.AspNetCore.Identity;
using Konscious.Security.Cryptography;
using System.Security.Cryptography;
using System.Text;

namespace AccessCity.API.Services.Security
{
    /// <summary>
    /// Argon2id password hashing implementation per OWASP 2025 recommendations.
    /// This provides significantly higher resistance to GPU/ASIC attacks compared to default PBKDF2.
    /// </summary>
    public class Argon2PasswordHasher<TUser> : IPasswordHasher<TUser> where TUser : class
    {
        private const int SaltSize = 16;
        private const int DegreeOfParallelism = 8;
        private const int Iterations = 4;
        private const int MemorySize = 65536; // 64MB

        public string HashPassword(TUser user, string password)
        {
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var hash = GenerateHash(password, salt);
            
            return $"{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
        }

        public PasswordVerificationResult VerifyHashedPassword(TUser user, string hashedPassword, string providedPassword)
        {
            var segments = hashedPassword.Split('.');
            if (segments.Length != 2) return PasswordVerificationResult.Failed;

            var salt = Convert.FromBase64String(segments[0]);
            var hash = Convert.FromBase64String(segments[1]);

            var providedHash = GenerateHash(providedPassword, salt);

            return CryptographicOperations.FixedTimeEquals(hash, providedHash) 
                ? PasswordVerificationResult.Success 
                : PasswordVerificationResult.Failed;
        }

        private byte[] GenerateHash(string password, byte[] salt)
        {
            using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
            {
                Salt = salt,
                DegreeOfParallelism = DegreeOfParallelism,
                Iterations = Iterations,
                MemorySize = MemorySize
            };

            return argon2.GetBytes(32);
        }
    }
}
