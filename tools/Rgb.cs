// PrismSync native app - core color + math helpers (C# 5, .NET Framework 4.8)
using System;

namespace PrismSync
{
    /// <summary>One 8-bit RGB colour. Struct so frames are cheap to copy.</summary>
    public struct Rgb
    {
        public byte R;
        public byte G;
        public byte B;

        public Rgb(byte r, byte g, byte b) { R = r; G = g; B = b; }

        public static readonly Rgb Black = new Rgb(0, 0, 0);
        public static readonly Rgb White = new Rgb(255, 255, 255);

        public static Rgb FromArgb(int argb)
        {
            return new Rgb((byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
        }

        public int ToArgb()
        {
            return unchecked((int)0xFF000000) | (R << 16) | (G << 8) | B;
        }

        /// <summary>HSV with h in turns (0..1 wraps), s/v in 0..1.</summary>
        public static Rgb FromHsv(double h, double s, double v)
        {
            h = h - Math.Floor(h);
            if (s < 0) s = 0; else if (s > 1) s = 1;
            if (v < 0) v = 0; else if (v > 1) v = 1;
            double hh = h * 6.0;
            int i = (int)Math.Floor(hh);
            double f = hh - i;
            double p = v * (1 - s);
            double q = v * (1 - s * f);
            double t = v * (1 - s * (1 - f));
            double r, g, b;
            switch (i % 6)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }
            return new Rgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
        }

        public Rgb Scale(double f)
        {
            if (f <= 0) return Black;
            if (f > 1) f = 1;
            return new Rgb((byte)(R * f), (byte)(G * f), (byte)(B * f));
        }

        public Rgb Dim(double f) { return Scale(f); }

        public static Rgb Lerp(Rgb a, Rgb b, double f)
        {
            if (f <= 0) return a;
            if (f >= 1) return b;
            return new Rgb((byte)(a.R + (b.R - a.R) * f),
                           (byte)(a.G + (b.G - a.G) * f),
                           (byte)(a.B + (b.B - a.B) * f));
        }

        public string ToHex() { return string.Format("#{0:X2}{1:X2}{2:X2}", R, G, B); }

        /// <summary>RGB -> HSV (h in turns 0..1, s/v in 0..1).</summary>
        public void ToHsv(out double h, out double s, out double v)
        {
            double r = R / 255.0, g = G / 255.0, b = B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double d = max - min;
            v = max;
            s = max <= 0.0001 ? 0 : d / max;
            if (d <= 0.0001) { h = 0; return; }
            if (max == r) h = ((g - b) / d) % 6.0;
            else if (max == g) h = (b - r) / d + 2.0;
            else h = (r - g) / d + 4.0;
            h = h / 6.0;
            if (h < 0) h += 1.0;
        }

        /// <summary>Quantise to a small, stable palette (used by the keyboard to
        /// keep per-frame packet count low without visible RGB-channel banding).</summary>
        public static Rgb QuantizeHsv(Rgb c, int hueSteps, int satLevels, int valSteps)
        {
            if (hueSteps < 1) hueSteps = 1;
            if (satLevels < 1) satLevels = 1;
            if (valSteps < 1) valSteps = 1;
            double h, s, v;
            c.ToHsv(out h, out s, out v);

            double hs = Math.Round(h * hueSteps) / hueSteps;
            // keep a true zero and one; otherwise quantise into a few levels
            double ss;
            if (s < 0.10) ss = 0;
            else ss = Fx.Clamp(Math.Round(s * satLevels) / satLevels, 0.45, 1.0);
            double vs = Math.Round(v * valSteps) / valSteps;
            if (vs < 0.06) return Black;
            return FromHsv(hs, ss, vs);
        }

        public static Rgb FromHex(string s)
        {
            if (string.IsNullOrEmpty(s)) return Black;
            s = s.Trim().TrimStart('#');
            if (s.Length == 3)
                s = new string(new char[] { s[0], s[0], s[1], s[1], s[2], s[2] });
            int v;
            if (s.Length == 6 && int.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out v))
                return FromArgb(v);
            return Black;
        }

        public override string ToString() { return ToHex(); }

        public static bool Same(Rgb a, Rgb b) { return a.R == b.R && a.G == b.G && a.B == b.B; }
    }

    /// <summary>Small math helpers shared by effects (keeps effect code short).</summary>
    public static class Fx
    {
        public const double Tau = Math.PI * 2.0;

        public static double Clamp01(double v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }
        public static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }
        public static double Lerp(double a, double b, double f) { return a + (b - a) * f; }

        /// <summary>0..1 triangle/sine wave with period 1 turn.</summary>
        public static double Sin01(double x) { return 0.5 + 0.5 * Math.Sin(x * Tau); }

        public static double Smoothstep(double f)
        {
            f = Clamp01(f);
            return f * f * (3.0 - 2.0 * f);
        }

        /// <summary>Deterministic pseudo random in 0..1 from two integers.</summary>
        public static double Hash(int a, int b)
        {
            unchecked
            {
                uint h = (uint)(a * 374761393 + b * 668265263);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / 16777215.0;
            }
        }

        /// <summary>Smooth 1D value noise in 0..1 (period-free, good enough for light textures).</summary>
        public static double Noise1(double x)
        {
            int i = (int)Math.Floor(x);
            double f = x - i;
            double a = Hash(i, 17);
            double b = Hash(i + 1, 17);
            return Lerp(a, b, Smoothstep(f));
        }

        /// <summary>Triangle distance on the 0..1 loop (0 at same point, 1 at the far side).</summary>
        public static double LoopDist(double a, double b)
        {
            double d = Math.Abs(a - b) % 1.0;
            return d > 0.5 ? 1.0 - d : d;
        }
    }
}
