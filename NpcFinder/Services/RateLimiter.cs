using System;
using System.Threading;
using System.Threading.Tasks;

namespace NpcFinder.Services
{
    public class RateLimiter
    {
        private readonly int _minDelayMs;
        private DateTime _last = DateTime.MinValue;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public RateLimiter(int minDelayMs)
        {
            _minDelayMs = Math.Max(0, minDelayMs);
        }

        public async Task WaitAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                var now = DateTime.UtcNow;
                var next = _last.AddMilliseconds(_minDelayMs);
                if (next > now)
                {
                    await Task.Delay(next - now, ct);
                }
                _last = DateTime.UtcNow;
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
