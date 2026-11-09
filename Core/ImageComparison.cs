using SkiaSharp;
using System;

namespace CUE4ParseCLI.Core
{
    public static class ImageComparison
    {
        /// <summary>
        /// Calculates Structural Similarity Index (SSIM) between two images
        /// Returns value between 0.0 (completely different) and 1.0 (identical)
        /// SSIM > 0.95 typically indicates visually identical images
        /// </summary>
        public static double CalculateSSIM(SKBitmap original, SKBitmap compressed)
        {
            if (original.Width != compressed.Width || original.Height != compressed.Height)
            {
                throw new ArgumentException("Images must have the same dimensions");
            }

            int width = original.Width;
            int height = original.Height;

            // SSIM constants
            const double k1 = 0.01;
            const double k2 = 0.03;
            const double L = 255; // Dynamic range of pixel values
            double c1 = (k1 * L) * (k1 * L);
            double c2 = (k2 * L) * (k2 * L);

            // Calculate means, variances, and covariance
            double meanX = 0, meanY = 0;
            int totalPixels = width * height;

            // First pass: calculate means
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var pixelX = original.GetPixel(x, y);
                    var pixelY = compressed.GetPixel(x, y);
                    
                    // Convert to grayscale for comparison
                    meanX += (pixelX.Red * 0.299 + pixelX.Green * 0.587 + pixelX.Blue * 0.114);
                    meanY += (pixelY.Red * 0.299 + pixelY.Green * 0.587 + pixelY.Blue * 0.114);
                }
            }

            meanX /= totalPixels;
            meanY /= totalPixels;

            // Second pass: calculate variances and covariance
            double varX = 0, varY = 0, covXY = 0;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var pixelX = original.GetPixel(x, y);
                    var pixelY = compressed.GetPixel(x, y);
                    
                    double grayX = (pixelX.Red * 0.299 + pixelX.Green * 0.587 + pixelX.Blue * 0.114);
                    double grayY = (pixelY.Red * 0.299 + pixelY.Green * 0.587 + pixelY.Blue * 0.114);

                    double diffX = grayX - meanX;
                    double diffY = grayY - meanY;

                    varX += diffX * diffX;
                    varY += diffY * diffY;
                    covXY += diffX * diffY;
                }
            }

            varX /= totalPixels;
            varY /= totalPixels;
            covXY /= totalPixels;

            // Calculate SSIM
            double numerator = (2 * meanX * meanY + c1) * (2 * covXY + c2);
            double denominator = (meanX * meanX + meanY * meanY + c1) * (varX + varY + c2);

            return numerator / denominator;
        }

        /// <summary>
        /// Calculates Peak Signal-to-Noise Ratio (PSNR) in decibels
        /// Higher is better. Typical values:
        /// 40-50 dB: Excellent quality
        /// 30-40 dB: Good quality
        /// 20-30 dB: Acceptable quality
        /// < 20 dB: Poor quality
        /// </summary>
        public static double CalculatePSNR(SKBitmap original, SKBitmap compressed)
        {
            if (original.Width != compressed.Width || original.Height != compressed.Height)
            {
                throw new ArgumentException("Images must have the same dimensions");
            }

            double mse = CalculateMSE(original, compressed);
            
            if (mse == 0)
                return double.PositiveInfinity; // Images are identical
            
            const double maxPixelValue = 255.0;
            return 20 * Math.Log10(maxPixelValue / Math.Sqrt(mse));
        }

        /// <summary>
        /// Calculates Mean Squared Error between two images
        /// Lower is better. 0 = identical images
        /// </summary>
        public static double CalculateMSE(SKBitmap original, SKBitmap compressed)
        {
            if (original.Width != compressed.Width || original.Height != compressed.Height)
            {
                throw new ArgumentException("Images must have the same dimensions");
            }

            int width = original.Width;
            int height = original.Height;
            double mse = 0;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var pixelX = original.GetPixel(x, y);
                    var pixelY = compressed.GetPixel(x, y);
                    
                    // Calculate MSE for each channel
                    double diffR = pixelX.Red - pixelY.Red;
                    double diffG = pixelX.Green - pixelY.Green;
                    double diffB = pixelX.Blue - pixelY.Blue;
                    
                    mse += (diffR * diffR + diffG * diffG + diffB * diffB) / 3.0;
                }
            }

            return mse / (width * height);
        }

        /// <summary>
        /// Validates image quality and returns a quality report
        /// </summary>
        public static ImageQualityReport CompareImages(SKBitmap original, SKBitmap compressed, string format, int quality)
        {
            var ssim = CalculateSSIM(original, compressed);
            var psnr = CalculatePSNR(original, compressed);
            var mse = CalculateMSE(original, compressed);

            return new ImageQualityReport
            {
                Format = format,
                Quality = quality,
                SSIM = ssim,
                PSNR = psnr,
                MSE = mse,
                IsVisuallyIdentical = ssim > 0.95,
                IsNearLossless = ssim > 0.99 && psnr > 40,
                IsLossless = mse == 0
            };
        }
    }

    public class ImageQualityReport
    {
        public string Format { get; set; } = "";
        public int Quality { get; set; }
        public double SSIM { get; set; }
        public double PSNR { get; set; }
        public double MSE { get; set; }
        public bool IsVisuallyIdentical { get; set; }
        public bool IsNearLossless { get; set; }
        public bool IsLossless { get; set; }

        public override string ToString()
        {
            return $"Quality Report [{Format} Q{Quality}]:\n" +
                   $"  SSIM: {SSIM:F4} {(IsVisuallyIdentical ? "✓ Visually Identical" : "✗ Visible Difference")}\n" +
                   $"  PSNR: {(double.IsPositiveInfinity(PSNR) ? "∞ (Lossless)" : $"{PSNR:F2} dB")}\n" +
                   $"  MSE: {MSE:F2} {(IsLossless ? "(Perfect match)" : "")}\n" +
                   $"  Quality: {(IsLossless ? "Lossless" : IsNearLossless ? "Near-lossless" : IsVisuallyIdentical ? "Visually identical" : "Lossy")}";
        }
    }
}
