using System.IO;
using System.Text.Json;

namespace NpcFinder.Services
{
    public class CacheStore
    {
        private readonly string _root;

        public CacheStore(string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
            {
                rootDirectory = Path.Combine(Path.GetTempPath(), "NpcFinderCache");
            }

            _root = rootDirectory;
            Directory.CreateDirectory(_root);
        }

        private string PathFor(string key)
        {
            // key -> safe filename
            var safe = key.Replace(":", "_").Replace("/", "_").Replace("\\", "_");
            return System.IO.Path.Combine(_root, safe + ".json");
        }

        public bool TryLoad<T>(string key, out T value)
        {
            value = default;
            try
            {
                var p = PathFor(key);
                if (!File.Exists(p)) return false;

                var json = File.ReadAllText(p);
                value = JsonSerializer.Deserialize<T>(json);
                return value != null;
            }
            catch
            {
                return false;
            }
        }

        public void Save<T>(string key, T value)
        {
            try
            {
                var p = PathFor(key);
                var json = JsonSerializer.Serialize(value);
                File.WriteAllText(p, json);
            }
            catch
            {
                
            }
        }
    }
}
