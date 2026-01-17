using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NpcFinder.Util
{
    internal sealed class SimpleDiskCache
    {
        private readonly string _rootDir;

        public SimpleDiskCache(string rootDir)
        {
            _rootDir = rootDir ?? throw new ArgumentNullException(nameof(rootDir));
            Directory.CreateDirectory(_rootDir);
        }

        private static string SafeFileName(string key)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                key = key.Replace(c, '_');
            return key;
        }

        private string PathFor(string prefix, string key)
        {
            return System.IO.Path.Combine(_rootDir, prefix + "_" + SafeFileName(key) + ".json");
        }

        public async Task<T> TryGetAsync<T>(string prefix, string key, TimeSpan maxAge, CancellationToken ct) where T : class
        {
            try
            {
                string path = PathFor(prefix, key);
                if (!File.Exists(path)) return null;

                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
                if (age > maxAge) return null;

                using (var fs = File.OpenRead(path))
                {
                    return await JsonSerializer.DeserializeAsync<T>(fs, cancellationToken: ct).ConfigureAwait(false);
                }
            }
            catch
            {
                return null;
            }
        }

        public async Task PutAsync<T>(string prefix, string key, T value, CancellationToken ct)
        {
            try
            {
                string path = PathFor(prefix, key);

                // atomic-ish write
                string tmp = path + ".tmp";
                using (var fs = File.Create(tmp))
                {
                    await JsonSerializer.SerializeAsync(fs, value, cancellationToken: ct).ConfigureAwait(false);
                }

                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch
            {
                // ignore cache write failures
            }
        }
    }
}
