using System;
using System.IO;

namespace RevitMCP.Core.Walkthrough
{
    /// <summary>2x3 affine transform: px = A*x + B*y + Tx ; py = C*x + D*y + Ty.</summary>
    public readonly struct Affine2D
    {
        public Affine2D(double a, double b, double tx, double c, double d, double ty)
        { A = a; B = b; Tx = tx; C = c; D = d; Ty = ty; }

        public double A { get; }
        public double B { get; }
        public double Tx { get; }
        public double C { get; }
        public double D { get; }
        public double Ty { get; }

        public (double X, double Y) Apply(double x, double y) => (A * x + B * y + Tx, C * x + D * y + Ty);

        public Affine2D Inverse()
        {
            var det = A * D - B * C;
            if (Math.Abs(det) < 1e-18)
            {
                throw new InvalidOperationException("Affine transform is not invertible.");
            }

            var ia = D / det;
            var ib = -B / det;
            var ic = -C / det;
            var id = A / det;
            return new Affine2D(ia, ib, -(ia * Tx + ib * Ty), ic, id, -(ic * Tx + id * Ty));
        }

        /// <summary>Contract order: [a, b, tx, c, d, ty].</summary>
        public double[] ToArray(int decimals = 9) => new[]
        {
            Math.Round(A, decimals), Math.Round(B, decimals), Math.Round(Tx, decimals),
            Math.Round(C, decimals), Math.Round(D, decimals), Math.Round(Ty, decimals)
        };
    }

    /// <summary>Section-plane coordinates of a model point.</summary>
    public readonly struct FramePoint
    {
        public FramePoint(double u, double v, double depth) { U = u; V = v; Depth = depth; }
        public double U { get; }
        public double V { get; }
        public double Depth { get; }
    }

    /// <summary>
    /// Pixel registration between exported view images and model coordinates.
    /// View crop boxes are expressed in a local frame (X right, Y up); exported images have Y down.
    /// </summary>
    public static class PlanProjection
    {
        /// <summary>
        /// Model (x, y) to image pixel mapping for a view whose crop rectangle [min, max] is expressed in the
        /// local frame (origin, basisX, basisY) and exported edge-to-edge into an image of the given size.
        /// All lengths must share one unit (meters in the walkthrough exporter).
        /// </summary>
        public static Affine2D ModelToPixel(
            double originX, double originY,
            double basisXx, double basisXy,
            double basisYx, double basisYy,
            double cropMinX, double cropMinY, double cropMaxX, double cropMaxY,
            int imageWidth, int imageHeight)
        {
            var width = cropMaxX - cropMinX;
            var height = cropMaxY - cropMinY;
            if (width <= 0 || height <= 0 || imageWidth <= 0 || imageHeight <= 0)
            {
                throw new ArgumentException("Crop rectangle and image size must be positive.");
            }

            var sx = imageWidth / width;
            var sy = imageHeight / height;

            // lx = bx.x*(x-ox) + bx.y*(y-oy) ; ly = by.x*(x-ox) + by.y*(y-oy)
            // px = (lx - minX) * sx ; py = (maxY - ly) * sy
            var a = sx * basisXx;
            var b = sx * basisXy;
            var tx = -sx * (basisXx * originX + basisXy * originY) - sx * cropMinX;
            var c = -sy * basisYx;
            var d = -sy * basisYy;
            var ty = sy * (basisYx * originX + basisYy * originY) + sy * cropMaxY;
            return new Affine2D(a, b, tx, c, d, ty);
        }

        /// <summary>u = dot(p - origin, right), v = dot(p - origin, up), depth = dot(p - origin, normal).</summary>
        public static FramePoint ProjectToFrame(Vec3 point, Vec3 origin, Vec3 right, Vec3 up, Vec3 normal)
        {
            var delta = point - origin;
            return new FramePoint(delta.Dot(right), delta.Dot(up), delta.Dot(normal));
        }

        /// <summary>Relative difference between crop aspect ratio and image aspect ratio (0 = identical).</summary>
        public static double AspectMismatch(double cropWidth, double cropHeight, int imageWidth, int imageHeight)
        {
            if (cropWidth <= 0 || cropHeight <= 0 || imageWidth <= 0 || imageHeight <= 0)
            {
                return double.PositiveInfinity;
            }

            var cropAspect = cropWidth / cropHeight;
            var imageAspect = (double)imageWidth / imageHeight;
            return Math.Abs(cropAspect - imageAspect) / cropAspect;
        }

        /// <summary>Reads width/height from a PNG IHDR chunk without decoding the image.</summary>
        public static bool TryReadPngSize(Stream stream, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (stream == null)
            {
                return false;
            }

            var header = new byte[24];
            var read = 0;
            while (read < header.Length)
            {
                var n = stream.Read(header, read, header.Length - read);
                if (n <= 0)
                {
                    return false;
                }

                read += n;
            }

            byte[] signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            for (var i = 0; i < signature.Length; i++)
            {
                if (header[i] != signature[i])
                {
                    return false;
                }
            }

            if (header[12] != (byte)'I' || header[13] != (byte)'H' || header[14] != (byte)'D' || header[15] != (byte)'R')
            {
                return false;
            }

            width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
            height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
            return width > 0 && height > 0;
        }

        public static bool TryReadPngSize(string path, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (!File.Exists(path))
            {
                return false;
            }

            using (var stream = File.OpenRead(path))
            {
                return TryReadPngSize(stream, out width, out height);
            }
        }
    }
}
