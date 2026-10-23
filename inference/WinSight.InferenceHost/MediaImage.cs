namespace WinSight.InferenceHost;

internal static class MediaImage
{
    internal static VisionInputs Load(string path, int softTokens)
    {
        // Bound tensor allocation; 140 is used for video, 280 for still images in the pinned profile.
        if (softTokens < 1 || softTokens > 1120)
            throw new ArgumentOutOfRangeException(nameof(softTokens), "Expected 1..1120 soft tokens.");
        MediaRgb source = MediaWic.Read(path);
        int maxPatches = checked(softTokens * 9);
        double factor = Math.Sqrt(maxPatches * 256.0 / ((double)source.Width * source.Height));
        int height = (int)Math.Floor(factor * source.Height / 48) * 48;
        int width = (int)Math.Floor(factor * source.Width / 48) * 48;
        if (height == 0 && width == 0) throw new InvalidDataException("Image target has zero area.");
        int maxSide = softTokens * 48;
        if (height == 0)
        {
            height = 48;
            width = Math.Min(source.Width / source.Height * 48, maxSide);
        }
        else if (width == 0)
        {
            width = 48;
            height = Math.Min(source.Height / source.Width * 48, maxSide);
        }
        MediaRgb image = MediaBicubic.Resize(source, width, height);
        var pixels = new float[checked(maxPatches * 768)];
        var positions = new long[checked(maxPatches * 2)];
        Array.Fill(positions, -1);
        int patch = 0;
        for (int row = 0; row < height / 16; ++row)
            for (int col = 0; col < width / 16; ++col)
            {
                positions[patch * 2] = col;
                positions[patch * 2 + 1] = row;
                int dest = patch++ * 768;
                for (int dy = 0; dy < 16; ++dy)
                    for (int dx = 0; dx < 16; ++dx)
                    {
                        int src = ((row * 16 + dy) * width + col * 16 + dx) * 3;
                        for (int c = 0; c < 3; ++c)
                            pixels[dest++] = (float)(image.Data[src + c] * (1.0 / 255));
                    }
            }
        return new VisionInputs(pixels, positions, maxPatches);
    }
}
