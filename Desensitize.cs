using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Desensitize
{
    /// <summary>
    /// Desensitize - keeps zoomed-in range rings on the tactical map from slowing down or
    /// crashing the game, particularly under Wine.
    ///
    /// Aurora draws sensor, fire control and passive detection rings as dashed ellipses whose
    /// pixel radius is range divided by map scale.  Zoomed in far enough, that radius reaches
    /// millions of pixels.  GDI+ then has to build and dash a path that is mostly off screen,
    /// which is extremely slow and can exhaust memory.  This patch intercepts those draw calls
    /// and either skips rings that cannot be seen or draws only the visible part of the arc.
    /// </summary>
    public class Desensitize : AuroraPatch.Patch
    {
        public override string Description => "Prevents slowdowns and crashes from huge dashed range rings when zoomed in";
        public override IEnumerable<string> Dependencies => new string[0];

        protected override void Loaded(Harmony harmony)
        {
            try
            {
                var drawEllipse = AccessTools.Method(typeof(Graphics), "DrawEllipse",
                    new[] { typeof(Pen), typeof(float), typeof(float), typeof(float), typeof(float) });

                if (drawEllipse == null)
                {
                    LogError("Desensitize: Graphics.DrawEllipse(Pen, float, float, float, float) not found");
                    return;
                }

                var prefix = new HarmonyMethod(typeof(RingClipper).GetMethod("Prefix", AccessTools.all));

                // Run after any other patch that adjusts the pen, so they still see the call.
                prefix.priority = Priority.Last;

                harmony.Patch(drawEllipse, prefix: prefix);
                LogInfo("Desensitize: range ring clipping enabled");
            }
            catch (Exception e)
            {
                LogError("Desensitize: failed to patch Graphics.DrawEllipse: " + e);
            }
        }
    }

    internal static class RingClipper
    {
        // Rings with a radius at or below this are drawn normally.  Larger rings are
        // clipped to the visible area.  Lower this if the map is still slow, raise it if
        // you see dash patterns change as you zoom.
        private const double ClipRadius = 1500.0;

        // Argument names are positional (__0 to __4) so they do not depend on the parameter
        // names used by the System.Drawing implementation in use.
        private static bool Prefix(Graphics __instance, Pen __0, float __1, float __2, float __3, float __4)
        {
            Pen pen = __0;
            float x = __1, y = __2, width = __3, height = __4;

            if (pen == null)
                return true;

            // Only dashed pens are a problem, so leave solid rings to GDI+.
            if (pen.DashStyle == DashStyle.Solid)
                return true;

            // A non-finite ring cannot be drawn meaningfully and can destabilize GDI+.
            if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(width) || float.IsInfinity(width))
                return false;

            // Only circles are handled here.
            if (Math.Abs(width - height) > 1f)
                return true;

            try
            {
                // Handle returns true when it drew or skipped the ring, so suppress the original.
                return !Handle(__instance, pen, x, y, width);
            }
            catch
            {
                return true;
            }
        }

        // Returns true if the ring was skipped or drawn here.  Returns false to let GDI+ draw it.
        private static bool Handle(Graphics g, Pen pen, double x, double y, double d)
        {
            double r = d / 2, cx = x + r, cy = y + r;

            RectangleF v = g.VisibleClipBounds;
            v.Inflate(50f, 50f);

            // Distance from the ring center to the nearest and farthest point of the visible area.
            double nx = Math.Max(v.Left, Math.Min(cx, v.Right));
            double ny = Math.Max(v.Top, Math.Min(cy, v.Bottom));
            double dMin = Math.Sqrt((cx - nx) * (cx - nx) + (cy - ny) * (cy - ny));

            double[] px = { v.Left, v.Right, v.Right, v.Left };
            double[] py = { v.Top, v.Top, v.Bottom, v.Bottom };
            double dMax = 0;
            for (int i = 0; i < 4; i++)
                dMax = Math.Max(dMax, Math.Sqrt((cx - px[i]) * (cx - px[i]) + (cy - py[i]) * (cy - py[i])));

            // The ring only touches the visible area when dMin <= r <= dMax.
            if (r < dMin || r > dMax)
                return true;

            // Small enough that GDI+ handles it fine.
            if (r <= ClipRadius)
                return false;

            // Angular span of the visible area as seen from the ring center.
            double a0 = Math.Atan2((v.Top + v.Bottom) / 2 - cy, (v.Left + v.Right) / 2 - cx);
            double lo = 0, hi = 0;
            for (int i = 0; i < 4; i++)
            {
                double da = Math.Atan2(py[i] - cy, px[i] - cx) - a0;
                while (da > Math.PI) da -= 2 * Math.PI;
                while (da < -Math.PI) da += 2 * Math.PI;
                lo = Math.Min(lo, da);
                hi = Math.Max(hi, da);
            }

            // Draw only the visible arc as a short polyline.
            const int steps = 96;
            var pts = new PointF[steps + 1];
            for (int i = 0; i <= steps; i++)
            {
                double a = a0 + lo + (hi - lo) * i / steps;
                pts[i] = new PointF((float)(cx + r * Math.Cos(a)), (float)(cy + r * Math.Sin(a)));
            }

            g.DrawLines(pen, pts);
            return true;
        }
    }
}
