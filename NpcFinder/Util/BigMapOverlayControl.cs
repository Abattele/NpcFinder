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


        private static readonly bool DEBUG_LOGS = false;



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


        private static float GetUiScaleMultiplier()
        {
            try
            {
                object gfx = GameService.Graphics;
                if (gfx == null) return 1f;

                var t = gfx.GetType();

                var p =
                    t.GetProperty("UIScaleMultiplier") ??
                    t.GetProperty("UiScaleMultiplier") ??
                    t.GetProperty("ScaleMultiplier") ??
                    t.GetProperty("UIScale") ??
                    t.GetProperty("UiScale");

                if (p == null) return 1f;

                var v = p.GetValue(gfx);
                if (v == null) return 1f;

                if (v is float f) return f;
                if (v is double d) return (float)d;

                return Convert.ToSingle(v);
            }
            catch
            {
                return 1f;
            }
        }

        private Vector2? ContinentToScreen(
            double cx,
            double cy,
            Rectangle bounds,
            float centerX,
            float centerY,
            float scale
        )
        {
            if (float.IsNaN(scale) || float.IsInfinity(scale) || Math.Abs(scale) < 1e-6f)
                return null;

            float uiMul = GetUiScaleMultiplier();
            if (float.IsNaN(uiMul) || float.IsInfinity(uiMul) || uiMul <= 1e-6f)
                uiMul = 1f;

            var mapPixelCenter = new Vector2(
                bounds.X + bounds.Width / 2f,
                bounds.Y + bounds.Height / 2f
            );

            float dx = (float)cx - centerX;
            float dy = (float)cy - centerY;

            // correct space conversion
            float px = mapPixelCenter.X + (dx / scale) / uiMul;
            float py = mapPixelCenter.Y + (dy / scale) / uiMul;

            if (float.IsNaN(px) || float.IsInfinity(px) || float.IsNaN(py) || float.IsInfinity(py))
                return null;

            return new Vector2(px, py);
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
                start,
                null,
                color,
                angle,
                new Vector2(0f, 0.5f),
                new Vector2(length, thickness),
                SpriteEffects.None,
                0f
            );
        }


        // ----- helpers and misc methods ----- //

        private static Vector2 ClampToBounds(Vector2 p, Rectangle b, float margin)
        {
            float x = MathHelper.Clamp(p.X, b.Left + margin, b.Right - margin);
            float y = MathHelper.Clamp(p.Y, b.Top + margin, b.Bottom - margin);
            return new Vector2(x, y);
        }

        private void DrawArrow(SpriteBatch sb, Vector2 tip, Vector2 dir, float size, float thickness, Color color)
        {
            // V-shaped arrow head
            // tip = where arrow points, dir = direction toward target
            var left = Rotate(dir, +2.6f);   // ~150 dgrs
            var right = Rotate(dir, -2.6f);

            var a = tip - left * size;
            var b = tip - right * size;

            DrawLine(sb, a, tip, thickness, color);
            DrawLine(sb, b, tip, thickness, color);
        }

        private static Vector2 Rotate(Vector2 v, float radians)
        {
            float c = (float)Math.Cos(radians);
            float s = (float)Math.Sin(radians);
            return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
        }


        protected override void Paint(SpriteBatch spriteBatch, Rectangle bounds)
        {
            // only draw when big map is open
            if (!(GameService.Gw2Mumble?.UI?.IsMapOpen ?? false))
                return;

            var tp = TargetProvider;
            if (tp == null) return;

            NpcTarget target;
            try { target = tp(); } catch { return; }
            if (target == null) return;

            // gate by continent
            if (CurrentContinentIdProvider != null)
            {
                var curCont = CurrentContinentIdProvider();
                if (curCont != 0 && target.TargetContinentId != 0 && curCont != target.TargetContinentId)
                    return;
            }

            // need current Mumble map center/scale
            float centerX, centerY, scale;
            if (!MumbleReader.TryGetWorldMapUi(out centerX, out centerY, out scale))
                return;

            if (float.IsNaN(scale) || float.IsInfinity(scale) || Math.Abs(scale) < 1e-6f)
                return;

            // ensure pixel texture exists
            if (_pixel == null)
            {
                _pixel = new Texture2D(spriteBatch.GraphicsDevice, 1, 1);
                _pixel.SetData(new[] { Color.White });
            }


            // convert to screen
            var posOpt = ContinentToScreen(
                target.TargetContinentX,
                target.TargetContinentY,
                bounds,
                centerX,
                centerY,
                scale
            );

            if (!posOpt.HasValue)
                return;

            var pos = posOpt.Value;


            if ((_dbgEvery++ % 60) == 0 && DEBUG_LOGS)
            {
                Log.Warn($"[OverlayDbg] center=({centerX},{centerY}) scale={scale} " +
                         $"target=({target.TargetContinentX},{target.TargetContinentY}) " +
                         $"dxdy=({(float)target.TargetContinentX - centerX},{(float)target.TargetContinentY - centerY}) " +
                         $"screen=({pos.X},{pos.Y})");
            }

            // if offscreen -> clamp to edge and draw an arrow toward target
            const float margin = 18f;

            bool offscreen = (pos.X < bounds.Left + margin) || (pos.X > bounds.Right - margin) ||
                             (pos.Y < bounds.Top + margin) || (pos.Y > bounds.Bottom - margin);

            if (offscreen)
            {
                var clamped = ClampToBounds(pos, bounds, margin);

                // direction from map center (screen center) to target screen position
                var mapCenter = new Vector2(bounds.X + bounds.Width / 2f, bounds.Y + bounds.Height / 2f);
                var dir = pos - mapCenter;

                if (dir.LengthSquared() < 0.001f)
                    dir = new Vector2(1, 0);
                else
                    dir.Normalize();

                // edge indicator
                DrawRing(spriteBatch, clamped, 18f, 3f, Color.Yellow);
                DrawArrow(spriteBatch, clamped, dir, 16f, 3f, Color.Yellow);
                return;
            }

            // normal onscreen draw
            DrawRing(spriteBatch, pos, 22f, 3f, Color.Yellow);
            DrawCross(spriteBatch, pos, 10f, 2f, Color.Yellow);
        }

    }
}
