using System.Collections;
using System.Security.Cryptography;
using System.Text;

namespace AccessCity.API.Services
{
    public interface IBloomFilterService
    {
        void Add(string item);
        bool MightContain(string item);
    }

    /// <summary>
    /// A probabilistic filter to prevent cache/DB penetration for non-existent geographic keys.
    /// Optimal for the "Definitely Not" check before expensive operations.
    /// </summary>
    public class BloomFilterService : IBloomFilterService
    {
        private readonly BitArray _bitArray;
        private readonly int _size;
        private readonly int _hashCount;
        private readonly object _lock = new();

        public BloomFilterService(int expectedItems = 100000, double falsePositiveProbability = 0.01)
        {
            // Optimal size m = -(n * ln(p)) / (ln(2)^2)
            _size = (int)(-(expectedItems * Math.Log(falsePositiveProbability)) / Math.Pow(Math.Log(2), 2));
            
            // Optimal hash functions k = (m/n) * ln(2)
            _hashCount = (int)Math.Max(1, Math.Round((double)_size / expectedItems * Math.Log(2)));
            
            _bitArray = new BitArray(_size);
        }

        public void Add(string item)
        {
            var positions = GetPositions(item);
            lock (_lock)
            {
                foreach (var pos in positions)
                {
                    _bitArray.Set(pos, true);
                }
            }
        }

        public bool MightContain(string item)
        {
            var positions = GetPositions(item);
            lock (_lock)
            {
                foreach (var pos in positions)
                {
                    if (!_bitArray.Get(pos)) return false;
                }
            }
            return true;
        }

        private IEnumerable<int> GetPositions(string item)
        {
            byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(item));
            int hash1 = BitConverter.ToInt32(hashBytes, 0);
            int hash2 = BitConverter.ToInt32(hashBytes, 4);

            for (int i = 0; i < _hashCount; i++)
            {
                // Double hashing: pos = (hash1 + i * hash2) % size
                int pos = Math.Abs((hash1 + i * hash2) % _size);
                yield return pos;
            }
        }
    }
}
