using System;
using NpcFinder.Models;

namespace NpcFinder.Util
{
    public static class CoordConverter
    {
        public static (double cx, double cy) MapToContinent(double mapX, double mapY, Rect2D mapRect, Rect2D contRect)
        {
            var dx = (mapRect.X2 - mapRect.X1);
            var dy = (mapRect.Y2 - mapRect.Y1);
            if (Math.Abs(dx) < 0.000001 || Math.Abs(dy) < 0.000001) return (0, 0);

            var nx = (mapX - mapRect.X1) / dx;
            var ny = (mapRect.Y2 - mapY) / dy; // invert Y

            var cx = contRect.X1 + nx * (contRect.X2 - contRect.X1);
            var cy = contRect.Y1 + ny * (contRect.Y2 - contRect.Y1);

            if (double.IsNaN(cx) || double.IsNaN(cy) || double.IsInfinity(cx) || double.IsInfinity(cy)) return (0, 0);
            return (cx, cy);
        }
    }
}
