namespace WinSight.InferenceHost;

// Catmull-Rom numerical contract consulted from
// libvips 8.18.7 (24ad4d042940e6bf99a68871ba886ca8847c9c82): 64 phases, Q12
// coefficients, two rounded separable passes, black extension, integer-corner affine mapping.
// This is not WIC's cubic interpolation. See docs/media-attribution.md for the open distribution review.
internal static class MediaBicubic
{
    private static readonly int[,] Coefficients = CreateCoefficients();

    internal static MediaRgb Resize(MediaRgb source, int width, int height)
    {
        if (source.Width == width && source.Height == height) return source;
        var result = new byte[checked(width * height * 3)];
        double ax = (double)width / source.Width, ay = (double)height / source.Height;
        double determinant = ax * ay;
        double stepX = ay / determinant, stepY = ax / determinant;
        for (int y = 0; y < height; ++y)
        {
            double sy = y * stepY + 2;
            int iy = (int)sy - 2;
            int phaseY = (((int)(sy * 128) & 127) + 1) >> 1;
            double sx = 2;
            for (int x = 0; x < width; ++x, sx += stepX)
            {
                int ix = (int)sx - 2;
                int phaseX = (((int)(sx * 128) & 127) + 1) >> 1;
                for (int c = 0; c < 3; ++c)
                {
                    int columnSum = 0;
                    for (int ky = 0; ky < 4; ++ky)
                    {
                        int rowSum = 0, yy = iy + ky - 1;
                        if (yy >= 0 && yy < source.Height)
                            for (int kx = 0; kx < 4; ++kx)
                            {
                                int xx = ix + kx - 1;
                                if (xx >= 0 && xx < source.Width)
                                    rowSum += Coefficients[phaseX, kx] * source.Data[(yy * source.Width + xx) * 3 + c];
                            }
                        columnSum += Coefficients[phaseY, ky] * ((rowSum + 2048) >> 12);
                    }
                    result[(y * width + x) * 3 + c] = (byte)Math.Clamp((columnSum + 2048) >> 12, 0, 255);
                }
            }
        }
        return new MediaRgb(result, width, height);
    }

    private static int[,] CreateCoefficients()
    {
        var result = new int[65, 4];
        for (int phase = 0; phase <= 64; ++phase)
        {
            double t = phase / 64.0, t2 = t * t, t3 = t2 * t;
            result[phase, 0] = (int)(4096 * (-0.5 * t + t2 - 0.5 * t3));
            result[phase, 1] = (int)(4096 * (1 - 2.5 * t2 + 1.5 * t3));
            result[phase, 2] = (int)(4096 * (0.5 * t + 2 * t2 - 1.5 * t3));
            result[phase, 3] = (int)(4096 * (-0.5 * t2 + 0.5 * t3));
        }
        return result;
    }
}
