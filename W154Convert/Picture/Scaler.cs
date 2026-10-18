namespace W154Convert;

static class Scaler
{
    public static byte[] ToSquareRgb24(ReadOnlySpan<byte> rgb, int width, int height, FitMode fit)
    {
        if (width <= 0 || height <= 0 || rgb.Length < width * height * 3)
            throw new W154Exception("quadro RGB24 inválido.");

        var square = new byte[PictureContract.Pixels * 3];
        if (fit == FitMode.Cover)
            Cover(rgb, width, height, square);
        else
            Contain(rgb, width, height, square);
        return square;
    }

    public static ushort[] ToRgb565(ReadOnlySpan<byte> rgb, int width, int height, FitMode fit)
    {
        byte[] square = ToSquareRgb24(rgb, width, height, fit);
        var dst = new ushort[PictureContract.Pixels];
        for (int i = 0; i < PictureContract.Pixels; i++)
        {
            int p = i * 3;
            dst[i] = Rgb565.Pack(square[p], square[p + 1], square[p + 2]);
        }

        return dst;
    }

    static void Cover(ReadOnlySpan<byte> src, int width, int height, Span<byte> dst)
    {
        int side = Math.Min(width, height);
        int ox = (width - side) / 2;
        int oy = (height - side) / 2;
        if (side == PictureContract.Width)
        {
            CopyRect(src, width, ox, oy, side, side, dst, PictureContract.Width, 0, 0);
            return;
        }

        byte[] scaled = Resample(src, width, ox, oy, side, side, PictureContract.Width, PictureContract.Height);
        scaled.CopyTo(dst);
    }

    static void Contain(ReadOnlySpan<byte> src, int width, int height, Span<byte> dst)
    {
        dst.Clear();
        double scale = Math.Min(PictureContract.Width / (double)width, PictureContract.Height / (double)height);
        int dw = Math.Clamp((int)Math.Round(width * scale), 1, PictureContract.Width);
        int dh = Math.Clamp((int)Math.Round(height * scale), 1, PictureContract.Height);
        int ox = (PictureContract.Width - dw) / 2;
        int oy = (PictureContract.Height - dh) / 2;

        if (dw == width && dh == height)
        {
            CopyRect(src, width, 0, 0, width, height, dst, PictureContract.Width, ox, oy);
            return;
        }

        byte[] scaled = Resample(src, width, 0, 0, width, height, dw, dh);
        CopyRect(scaled, dw, 0, 0, dw, dh, dst, PictureContract.Width, ox, oy);
    }

    static void CopyRect(
        ReadOnlySpan<byte> src,
        int srcWidth,
        int sx,
        int sy,
        int width,
        int height,
        Span<byte> dst,
        int dstWidth,
        int dx,
        int dy)
    {
        for (int y = 0; y < height; y++)
        {
            int srcOff = ((sy + y) * srcWidth + sx) * 3;
            int dstOff = ((dy + y) * dstWidth + dx) * 3;
            src.Slice(srcOff, width * 3).CopyTo(dst.Slice(dstOff, width * 3));
        }
    }

    static byte[] Resample(
        ReadOnlySpan<byte> src,
        int stride,
        int sx,
        int sy,
        int sw,
        int sh,
        int dw,
        int dh)
    {
        var dst = new byte[dw * dh * 3];
        if (sw >= dw && sh >= dh)
            Area(src, stride, sx, sy, sw, sh, dst, dw, dh);
        else
            Bilinear(src, stride, sx, sy, sw, sh, dst, dw, dh);
        return dst;
    }

    static void Area(
        ReadOnlySpan<byte> src,
        int stride,
        int sx,
        int sy,
        int sw,
        int sh,
        Span<byte> dst,
        int dw,
        int dh)
    {
        for (int y = 0; y < dh; y++)
        {
            int y0 = sy + (y * sh / dh);
            int y1 = sy + ((y + 1) * sh / dh);
            if (y1 <= y0)
                y1 = y0 + 1;

            for (int x = 0; x < dw; x++)
            {
                int x0 = sx + (x * sw / dw);
                int x1 = sx + ((x + 1) * sw / dw);
                if (x1 <= x0)
                    x1 = x0 + 1;

                long r = 0;
                long g = 0;
                long b = 0;
                int n = 0;
                for (int yy = y0; yy < y1; yy++)
                {
                    int row = yy * stride;
                    for (int xx = x0; xx < x1; xx++)
                    {
                        int p = (row + xx) * 3;
                        r += src[p];
                        g += src[p + 1];
                        b += src[p + 2];
                        n++;
                    }
                }

                int o = (y * dw + x) * 3;
                dst[o] = (byte)(r / n);
                dst[o + 1] = (byte)(g / n);
                dst[o + 2] = (byte)(b / n);
            }
        }
    }

    static void Bilinear(
        ReadOnlySpan<byte> src,
        int stride,
        int sx,
        int sy,
        int sw,
        int sh,
        Span<byte> dst,
        int dw,
        int dh)
    {
        double maxX = sx + sw - 1;
        double maxY = sy + sh - 1;
        for (int y = 0; y < dh; y++)
        {
            double fy = sy + ((y + 0.5) * sh / dh) - 0.5;
            if (fy < sy)
                fy = sy;
            if (fy > maxY)
                fy = maxY;
            int y0 = (int)Math.Floor(fy);
            int y1 = Math.Min(y0 + 1, sy + sh - 1);
            double ty = fy - y0;

            for (int x = 0; x < dw; x++)
            {
                double fx = sx + ((x + 0.5) * sw / dw) - 0.5;
                if (fx < sx)
                    fx = sx;
                if (fx > maxX)
                    fx = maxX;
                int x0 = (int)Math.Floor(fx);
                int x1 = Math.Min(x0 + 1, sx + sw - 1);
                double tx = fx - x0;

                Sample(src, stride, x0, y0, out int r00, out int g00, out int b00);
                Sample(src, stride, x1, y0, out int r10, out int g10, out int b10);
                Sample(src, stride, x0, y1, out int r01, out int g01, out int b01);
                Sample(src, stride, x1, y1, out int r11, out int g11, out int b11);

                int o = (y * dw + x) * 3;
                dst[o] = Mix(r00, r10, r01, r11, tx, ty);
                dst[o + 1] = Mix(g00, g10, g01, g11, tx, ty);
                dst[o + 2] = Mix(b00, b10, b01, b11, tx, ty);
            }
        }
    }

    static void Sample(ReadOnlySpan<byte> src, int stride, int x, int y, out int r, out int g, out int b)
    {
        int p = (y * stride + x) * 3;
        r = src[p];
        g = src[p + 1];
        b = src[p + 2];
    }

    static byte Mix(int c00, int c10, int c01, int c11, double tx, double ty)
    {
        double top = c00 + ((c10 - c00) * tx);
        double bottom = c01 + ((c11 - c01) * tx);
        double value = top + ((bottom - top) * ty);
        if (value < 0)
            value = 0;
        if (value > 255)
            value = 255;
        return (byte)Math.Round(value);
    }
}
