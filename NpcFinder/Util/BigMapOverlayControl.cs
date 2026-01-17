using System;
using Blish_HUD;
using Blish_HUD.Controls;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NpcFinder.Models;


namespace NpcFinder.Util
{

    public class BigMapOverlayControl : Control
    {

        private Texture2D _pixel;
        public Func<NpcTarget> TargetProvider;
        public Func<int> CurrentContinentIdProvider;
        private static readonly Blish_HUD.Logger Log = Blish_HUD.Logger.GetLogger<BigMapOverlayControl>();
        private int _dbgEvery = 0;
        private static DateTime _lastLog = DateTime.MinValue;

        public BigMapOverlayControl()
        {
            Location = new Point(0, 0);
            Size = GameService.Graphics.SpriteScreen.Size;
            Visible = true;
            Opacity = 1f;
            ZIndex = 10_000;
            ClipsBounds = false;
        }
        protected override CaptureType CapturesInput()
        {
            return CaptureType.None;
        }

        private Vector2? ContinentToScreen(double cx, double cy, Rectangle bounds)
        {
            float centerX, centerY, scale;
            if (!MumbleReader.TryGetWorldMapUi(out centerX, out centerY, out scale))
                return null;

            if (Math.Abs(scale) < 1e-6f)
                return null;

            var mapPixelCenter = new Vector2(
                bounds.X + bounds.Width / 2f,
                bounds.Y + bounds.Height / 2f
            );

            float dx = (float)cx - centerX;
            float dy = (float)cy - centerY;

            float px = mapPixelCenter.X + (dx / scale);
            float py = mapPixelCenter.Y + (dy / scale); 

            return new Vector2(px, py);
        }


        protected override void Paint(SpriteBatch spriteBatch, Rectangle bounds)
        {
            var tp = TargetProvider;
            if (tp == null) return;

            NpcTarget target;
            try { target = tp(); } catch { return; }
            if (target == null) return;

            // Gate by continent
            if (CurrentContinentIdProvider != null)
            {
                var curCont = CurrentContinentIdProvider();
                if (curCont != 0 && target.TargetContinentId != 0 && curCont != target.TargetContinentId)
                    return;
            }

            // Only draw when big map opens (prevents random transforms otherwise)
            if (!(GameService.Gw2Mumble?.UI?.IsMapOpen ?? false))
                return;

            var screenPos = ContinentToScreen(target.TargetContinentX, target.TargetContinentY, bounds);
            if (!screenPos.HasValue) return;

            var pos = screenPos.Value;
            float centerX, centerY, scale;
            if (!MumbleReader.TryGetWorldMapUi(out centerX, out centerY, out scale)) return;

            if ((_dbgEvery++ % 60) == 0)
            {
                Log.Warn($"[OverlayDbg] center=({centerX},{centerY}) scale={scale} " +
                         $"target=({target.TargetContinentX},{target.TargetContinentY}) " +
                         $"dxdy=({(float)target.TargetContinentX - centerX},{(float)target.TargetContinentY - centerY}) " +
                         $"screen=({pos.X},{pos.Y})");
            }


            if (_pixel == null)
            {
                _pixel = new Texture2D(spriteBatch.GraphicsDevice, 1, 1);
                _pixel.SetData(new[] { Color.White });
            }

            DrawRing(spriteBatch, pos, 22f, 3f, Color.Yellow);
            DrawCross(spriteBatch, pos, 10f, 2f, Color.Yellow);
        }



        private void DrawRing(SpriteBatch sb, Vector2 center, float radius, float thickness, Color color)
        {
            const int seg = 36;
            Vector2 prev = new Vector2(center.X + radius, center.Y);

            for (int i = 1; i <= seg; i++)
            {
                float a = (float)(Math.PI * 2 * i / seg);
                Vector2 cur = new Vector2(
                    center.X + (float)Math.Cos(a) * radius,
                    center.Y + (float)Math.Sin(a) * radius
                );
                DrawLine(sb, prev, cur, thickness, color);
                prev = cur;
            }
        }

        private void DrawCross(SpriteBatch sb, Vector2 c, float half, float thickness, Color color)
        {
            DrawLine(sb, new Vector2(c.X - half, c.Y), new Vector2(c.X + half, c.Y), thickness, color);
            DrawLine(sb, new Vector2(c.X, c.Y - half), new Vector2(c.X, c.Y + half), thickness, color);
        }

        private void DrawLine(SpriteBatch sb, Vector2 start, Vector2 end, float thickness, Color color)
        {
            Vector2 edge = end - start;
            float angle = (float)Math.Atan2(edge.Y, edge.X);
            float length = edge.Length();

            sb.Draw(
                _pixel,
                new Rectangle((int)start.X, (int)start.Y, (int)length, (int)thickness),
                null,
                color,
                angle,
                new Vector2(0, 0.5f),
                SpriteEffects.None,
                0f
            );
        }



        // ----- helpers and misc methods ----- //


        // no longer using this method, but i'm leaving it here for reference if i ever want to switch back... might use it for minimap overlay later
        private static bool ContinentToMap(NpcTarget target, out float mapX, out float mapY)
        {
            mapX = mapY = 0;
            var mi = target?.MapInfo;
            if (mi == null) return false;

            var mr = mi.MapRect;
            var cr = mi.ContinentRect;

            double cMinX = Math.Min(cr.X1, cr.X2);
            double cMaxX = Math.Max(cr.X1, cr.X2);
            double cMinY = Math.Min(cr.Y1, cr.Y2);
            double cMaxY = Math.Max(cr.Y1, cr.Y2);

            double mMinX = Math.Min(mr.X1, mr.X2);
            double mMaxX = Math.Max(mr.X1, mr.X2);
            double mMinY = Math.Min(mr.Y1, mr.Y2);
            double mMaxY = Math.Max(mr.Y1, mr.Y2);

            double cW = (cMaxX - cMinX);
            double cH = (cMaxY - cMinY);
            double mW = (mMaxX - mMinX);
            double mH = (mMaxY - mMinY);

            if (cW <= 0.000001 || cH <= 0.000001 || mW <= 0.000001 || mH <= 0.000001)
                return false;

            // normalized in continent rect
            double u = (target.TargetContinentX - cMinX) / cW;
            double v = (target.TargetContinentY - cMinY) / cH;

            u = Math.Max(0, Math.Min(1, u));
            v = Math.Max(0, Math.Min(1, v));
            v = 1.0 - v;

            mapX = (float)(mMinX + u * mW);
            mapY = (float)(mMinY + v * mH);
            return true;
        }


        // log method, using another dump now
        private static void LogOncePerSecond(Logger log, string msg)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastLog).TotalSeconds < 1) return;
            _lastLog = now;
            log.Warn(msg);
        }
    }
}
