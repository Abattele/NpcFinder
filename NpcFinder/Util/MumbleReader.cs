using System;
using Blish_HUD;
using Gw2Sharp.Models;

namespace NpcFinder.Util
{
    public static class MumbleReader
    {

        // refactored to remove reflections and use GameService.Gw2Mumble directly

        private static readonly bool DEBUG_LOGS = false;



        private static readonly Logger Log = Logger.GetLogger(typeof(MumbleReader));

        private static DateTime _lastWarn = DateTime.MinValue;
        private static DateTime _lastDumpUtc = DateTime.MinValue;
        private static bool _dumped;

        public static void ResetDiscovery() { }

        public static bool TryGetMapId(out int mapId)
        {
            mapId = 0;

            try
            {
                var mumble = GameService.Gw2Mumble;
                if (mumble == null) return false;

                mapId = mumble.CurrentMap.Id;

                return mapId > 0;
            }
            catch (Exception ex)
            {
                WarnOccasionally("[MumbleReader] TryGetMapId failed: " + ex.Message);
                return false;
            }
        }


        public static bool TryGetUiState(out uint uiState)
        {
            uiState = 0;

            return false;
        }


        public static bool TryGetWorldMapUi(out float centerX, out float centerY, out float scale)
        {
            centerX = centerY = 0f;
            scale = 1f;

            try
            {
                var ui = GameService.Gw2Mumble?.UI;
                if (ui == null) return false;

                scale = (float)ui.MapScale;
                if (float.IsNaN(scale) || float.IsInfinity(scale) || Math.Abs(scale) < 1e-6f)
                    return false;

                Coordinates2 c = ui.MapCenter;
                centerX = (float)c.X;
                centerY = (float)c.Y;

                return true;
            }
            catch (Exception ex)
            {
                WarnOccasionally("[MumbleReader] TryGetWorldMapUi failed: " + ex.Message);
                return false;
            }
        }

        public static void DumpUiOnce()
        {
            if (_dumped) return;
            _dumped = true;

            if (!DEBUG_LOGS) return;

            try
            {
                var ui = GameService.Gw2Mumble?.UI;
                if (ui == null)
                {
                    Log.Warn("[MumbleUI] GameService.Gw2Mumble.UI is null");
                    return;
                }

                Log.Warn($"[MumbleUI] IsMapOpen={ui.IsMapOpen}");
                Log.Warn($"[MumbleUI] MapScale={(float)ui.MapScale}");

                var c = ui.MapCenter;
                Log.Warn($"[MumbleUI] MapCenter=({(float)c.X},{(float)c.Y})");

            }
            catch (Exception ex)
            {
                Log.Warn("[MumbleUI] Dump failed: " + ex);
            }
        }

        public static void DumpUiOncePerSecond(bool requireMapOpen = true)
        {
            if (!DEBUG_LOGS) return;

            try
            {
                var ui = GameService.Gw2Mumble?.UI;
                if (ui == null)
                {
                    WarnOccasionally("[MumbleUI] GameService.Gw2Mumble.UI is null");
                    return;
                }

                if (requireMapOpen && !ui.IsMapOpen)
                    return;

                var now = DateTime.UtcNow;
                if ((now - _lastDumpUtc).TotalSeconds < 1.0)
                    return;
                _lastDumpUtc = now;

                var c = ui.MapCenter;
                Log.Warn($"[MumbleUI] open={ui.IsMapOpen} scale={(float)ui.MapScale} center=({(float)c.X},{(float)c.Y})");
            }
            catch (Exception ex)
            {
                WarnOccasionally("[MumbleUI] Dump failed: " + ex.Message);
            }
        }

        private static void WarnOccasionally(string msg)
        {
            if (!DEBUG_LOGS) return;

            var now = DateTime.UtcNow;
            if ((now - _lastWarn).TotalSeconds < 2) return;
            _lastWarn = now;

            Log.Warn(msg);
        }
    }
}