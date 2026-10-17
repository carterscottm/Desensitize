using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;

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
    /// and only lets a ring through when at least one pixel of it is in the viewport and its
    /// center is on screen or just outside the viewport.  Anything else is skipped and logged.
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

                // Give the static clipper a way to write to the AuroraPatch log, then decide
                // from the ini file whether it should log culls.
                RingClipper.Log = LogInfo;
                RingClipper.LogCulls = ReadLoggingSetting();

                var prefix = new HarmonyMethod(typeof(RingClipper).GetMethod("Prefix", AccessTools.all));

                // Run after any other patch that adjusts the pen, so they still see the call.
                prefix.priority = Priority.Last;

                harmony.Patch(drawEllipse, prefix: prefix);
                LogInfo("Desensitize: range ring culling enabled");
            }
            catch (Exception e)
            {
                LogError("Desensitize: failed to patch Graphics.DrawEllipse: " + e);
            }
        }

        // Reads Desensitize.ini from the same folder as this dll.  The file holds a single
        // line, logging=0 or logging=1.  Anything missing or unreadable means logging stays off.
        // The file is only read at launch, so changes need an Aurora restart.
        private bool ReadLoggingSetting()
        {
            try
            {
                string dllPath = typeof(Desensitize).Assembly.Location;
                if (string.IsNullOrEmpty(dllPath))
                {
                    LogInfo("Desensitize: could not determine dll location, cull logging disabled");
                    return false;
                }

                string iniPath = Path.ChangeExtension(dllPath, ".ini");
                if (!File.Exists(iniPath))
                {
                    LogInfo("Desensitize: " + iniPath + " not found, cull logging disabled");
                    return false;
                }

                bool enabled = false;

                foreach (string raw in File.ReadAllLines(iniPath))
                {
                    string line = raw.Trim();

                    // Skip blank lines and comments.
                    if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                        continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                        continue;

                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();

                    if (!key.Equals("logging", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (value == "1")
                        enabled = true;
                    else if (value == "0")
                        enabled = false;
                    else
                        LogError("Desensitize: logging must be 0 or 1 in " + iniPath + ", got '" + value + "'");
                }

                LogInfo("Desensitize: cull logging " + (enabled ? "enabled" : "disabled") + " (" + iniPath + ")");
                return enabled;
            }
            catch (Exception e)
            {
                LogError("Desensitize: failed to read ini file, cull logging disabled: " + e);
                return false;
            }
        }
    }

    internal static class RingClipper
    {
        // How far outside the viewport a ring center may sit and still be drawn, as a fraction
        // of the viewport's longest edge.  At 0.05 a 1920x1200 viewport allows a center up to
        // 96 px outside any edge.  Raise it to keep more off-screen-center rings on the map.
        private const double CenterMarginFraction = 0.05;

        // Set from Desensitize.ini at launch (logging=1 turns it on).  Off by default.
        internal static bool LogCulls;

        // The map redraws constantly while panning or zooming, and each redraw can cull many
        // rings, so logging is capped per second.  Culls past the cap are counted and reported
        // in a single summary line when the next window opens.
        private const int MaxLogsPerSecond = 20;

        // Set by the patch class so culls land in the AuroraPatch log.
        internal static Action<string> Log;

        private static readonly object LogLock = new object();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static long windowStartMs;
        private static int loggedInWindow;
        private static int suppressedInWindow;
        private static long totalCulled;

        // Argument names are positional (__0 to __4) so they do not depend on the parameter
        // names used by the System.Drawing implementation in use.
        // Returning true runs the original DrawEllipse.  Returning false suppresses it.
        private static bool Prefix(Graphics __instance, Pen __0, float __1, float __2, float __3, float __4)
        {
            Pen pen = __0;
            float x = __1, y = __2, width = __3, height = __4;

            if (pen == null)
                return true;

            // Only dashed pens are a problem, so leave solid rings to GDI+.
            if (pen.DashStyle == DashStyle.Solid)
                return true;

            try
            {
                // A non-finite ring cannot be drawn meaningfully and can destabilize GDI+.
                if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(y) || float.IsInfinity(y) ||
                    float.IsNaN(width) || float.IsInfinity(width))
                {
                    NoteCull("non-finite coordinates", x, y, width / 2.0, __instance.VisibleClipBounds);
                    return false;
                }

                // Only circles are handled here.
                if (Math.Abs(width - height) > 1f)
                    return true;

                return ShouldDraw(__instance, pen, x, y, width);
            }
            catch
            {
                // If the check itself fails, fall back to the game's normal behavior.
                return true;
            }
        }

        // A ring is drawn only if both of these hold:
        //   1. At least one pixel of the ring is inside the viewport.
        //   2. The ring center is inside the viewport, or within CenterMarginFraction of the
        //      longest viewport edge outside of it.
        //
        // Condition 2 is what keeps GDI+ safe.  With the center that close to the viewport and
        // some part of the ring inside it, the radius cannot exceed roughly the viewport
        // diagonal plus the margin, so GDI+ never has to build a huge path.
        private static bool ShouldDraw(Graphics g, Pen pen, double x, double y, double d)
        {
            double r = d / 2, cx = x + r, cy = y + r;

            RectangleF v = g.VisibleClipBounds;

            // Center must be in the viewport or within the allowed margin around it.
            double m = Math.Max(v.Width, v.Height) * CenterMarginFraction;
            if (cx < v.Left - m || cx > v.Right + m || cy < v.Top - m || cy > v.Bottom + m)
                return Cull("center outside viewport margin", cx, cy, r, v);

            // Distance from the center to the nearest and farthest point of the viewport.
            double nx = Math.Max(v.Left, Math.Min(cx, v.Right));
            double ny = Math.Max(v.Top, Math.Min(cy, v.Bottom));
            double dMin = Math.Sqrt((cx - nx) * (cx - nx) + (cy - ny) * (cy - ny));

            double fx = Math.Max(cx - v.Left, v.Right - cx);
            double fy = Math.Max(cy - v.Top, v.Bottom - cy);
            double dMax = Math.Sqrt(fx * fx + fy * fy);

            // The ring touches the viewport when dMin <= r <= dMax.  Half the pen width is added
            // on each side so a stroke that only just reaches the viewport still counts.
            double hw = pen.Width / 2.0;

            if (r < dMin - hw)
                return Cull("ring too small to reach viewport", cx, cy, r, v);

            if (r > dMax + hw)
                return Cull("ring encloses viewport", cx, cy, r, v);

            return true;
        }

        // Records a cull and returns false so callers can use it as their return value.
        private static bool Cull(string reason, double cx, double cy, double r, RectangleF v)
        {
            NoteCull(reason, cx, cy, r, v);
            return false;
        }

        private static void NoteCull(string reason, double cx, double cy, double r, RectangleF v)
        {
            if (!LogCulls || Log == null)
                return;

            long total;
            string summary = null;

            // Decide whether this cull gets its own line before building any strings, so culls
            // past the cap stay cheap.
            lock (LogLock)
            {
                total = ++totalCulled;
                long now = Clock.ElapsedMilliseconds;

                if (now - windowStartMs >= 1000)
                {
                    if (suppressedInWindow > 0)
                        summary = "Desensitize: " + suppressedInWindow.ToString(CultureInfo.InvariantCulture) +
                                  " more culls not logged in the previous second";

                    windowStartMs = now;
                    loggedInWindow = 0;
                    suppressedInWindow = 0;
                }

                if (loggedInWindow >= MaxLogsPerSecond)
                {
                    suppressedInWindow++;
                    return;
                }

                loggedInWindow++;
            }

            try
            {
                if (summary != null)
                    Log(summary);

                Log(string.Format(CultureInfo.InvariantCulture,
                    "Desensitize: culled ring ({0}) center=({1:F0},{2:F0}) radius={3:F0} viewport={4:F0}x{5:F0} total={6}",
                    reason, cx, cy, r, v.Width, v.Height, total));
            }
            catch
            {
                // Logging must never interfere with drawing.
            }
        }
    }
}