namespace InvoiceSorter.Core.Processing;

/// <summary>Image adjustments tried when text could not be read (faded thermal receipts, grey scans, dark copies).</summary>
public enum Enhancement
{
    None,
    /// <summary>Gamma 2.2: faint grey print becomes dark, white paper stays white.</summary>
    Darker,
    /// <summary>Stretches the 1–99 % brightness range to full black–white.</summary>
    Contrast,
    /// <summary>Contrast stretch followed by an Otsu threshold: pure black text on white.</summary>
    BlackWhite,
    /// <summary>Gamma 0.5: opens up dark, smudged or over-inked areas.</summary>
    Lighter,
}

/// <summary>Grayscale image adjustments on BGRA8 pixel buffers.</summary>
public static class ImageEnhancer
{
    public static string Describe(Enhancement e) => e switch
    {
        Enhancement.Darker => "sötétebb",
        Enhancement.Contrast => "nagyobb kontraszt",
        Enhancement.BlackWhite => "fekete-fehér",
        Enhancement.Lighter => "világosabb",
        _ => "normál",
    };

    /// <summary>Returns an adjusted grayscale copy of a BGRA8 buffer (the input is not modified).</summary>
    public static byte[] Apply(byte[] bgra, Enhancement enhancement)
    {
        var result = new byte[bgra.Length];
        if (enhancement == Enhancement.None)
        {
            Buffer.BlockCopy(bgra, 0, result, 0, bgra.Length);
            return result;
        }

        int pixels = bgra.Length / 4;
        var lum = new byte[pixels];
        for (int i = 0, o = 0; i < pixels; i++, o += 4)
            lum[i] = (byte)((bgra[o + 2] * 299 + bgra[o + 1] * 587 + bgra[o] * 114) / 1000);

        var map = new byte[256];
        switch (enhancement)
        {
            case Enhancement.Darker:
                for (int v = 0; v < 256; v++) map[v] = (byte)Math.Round(255 * Math.Pow(v / 255.0, 2.2));
                break;
            case Enhancement.Lighter:
                for (int v = 0; v < 256; v++) map[v] = (byte)Math.Round(255 * Math.Pow(v / 255.0, 0.5));
                break;
            case Enhancement.Contrast:
            case Enhancement.BlackWhite:
            {
                var histogram = Histogram(lum);
                int lo = Percentile(histogram, pixels, 0.01), hi = Percentile(histogram, pixels, 0.99);
                if (hi - lo < 10) { lo = 0; hi = 255; }
                for (int v = 0; v < 256; v++) map[v] = (byte)Math.Clamp((v - lo) * 255 / (hi - lo), 0, 255);
                if (enhancement == Enhancement.BlackWhite)
                {
                    var stretched = new int[256];
                    for (int v = 0; v < 256; v++) stretched[map[v]] += histogram[v];
                    int t = OtsuThreshold(stretched, pixels);
                    for (int v = 0; v < 256; v++) map[v] = map[v] > t ? (byte)255 : (byte)0;
                }
                break;
            }
        }

        for (int i = 0, o = 0; i < pixels; i++, o += 4)
        {
            var g = map[lum[i]];
            result[o] = result[o + 1] = result[o + 2] = g;
            result[o + 3] = 255;
        }
        return result;
    }

    private static int[] Histogram(byte[] lum)
    {
        var h = new int[256];
        foreach (var v in lum) h[v]++;
        return h;
    }

    private static int Percentile(int[] histogram, int total, double p)
    {
        long target = (long)(total * p), sum = 0;
        for (int v = 0; v < 256; v++)
        {
            sum += histogram[v];
            if (sum > target) return v;
        }
        return 255;
    }

    /// <summary>Otsu's method: the threshold that best separates dark (text) and light (paper) pixels.</summary>
    private static int OtsuThreshold(int[] histogram, int total)
    {
        double sumAll = 0;
        for (int v = 0; v < 256; v++) sumAll += v * (double)histogram[v];
        double sumBg = 0, best = -1;
        long weightBg = 0;
        int threshold = 128;
        for (int t = 0; t < 256; t++)
        {
            weightBg += histogram[t];
            if (weightBg == 0) continue;
            long weightFg = total - weightBg;
            if (weightFg == 0) break;
            sumBg += t * (double)histogram[t];
            double meanBg = sumBg / weightBg, meanFg = (sumAll - sumBg) / weightFg;
            double between = (double)weightBg * weightFg * (meanBg - meanFg) * (meanBg - meanFg);
            if (between > best) { best = between; threshold = t; }
        }
        return threshold;
    }
}
