namespace DinamikAda.Modules.Media;

/// <summary>
/// Allocation-light in-place radix-2 Cooley-Tukey FFT producing 16 logarithmic spectrum bands in [0, 1].
/// </summary>
public static class Radix2Fft
{
    public const int FftSize = 512;
    public const int BandCount = 16;

    public static void ComputeLogBands(ReadOnlySpan<float> monoSamples, Span<double> bandsOut, double[] prevSmoothed)
    {
        Span<double> real = stackalloc double[FftSize];
        Span<double> imag = stackalloc double[FftSize];

        int count = Math.Min(monoSamples.Length, FftSize);
        for (int i = 0; i < count; i++)
        {
            double hann = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FftSize - 1)));
            real[i] = monoSamples[i] * hann;
        }

        // Bit-reversal permutation
        int j = 0;
        for (int i = 0; i < FftSize; i++)
        {
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
            }
            int m = FftSize >> 1;
            while (m >= 1 && j >= m)
            {
                j -= m;
                m >>= 1;
            }
            j += m;
        }

        // Butterfly stages
        for (int len = 2; len <= FftSize; len <<= 1)
        {
            double angle = -2.0 * Math.PI / len;
            double wLenR = Math.Cos(angle);
            double wLenI = Math.Sin(angle);
            int half = len >> 1;

            for (int i = 0; i < FftSize; i += len)
            {
                double wR = 1.0, wI = 0.0;
                for (int k = 0; k < half; k++)
                {
                    int even = i + k;
                    int odd = even + half;
                    double tR = wR * real[odd] - wI * imag[odd];
                    double tI = wR * imag[odd] + wI * real[odd];
                    real[odd] = real[even] - tR;
                    imag[odd] = imag[even] - tI;
                    real[even] += tR;
                    imag[even] += tI;

                    double nextWR = wR * wLenR - wI * wLenI;
                    wI = wR * wLenI + wI * wLenR;
                    wR = nextWR;
                }
            }
        }

        // Map positive bins (1..255) logarithmically to 16 bands
        int prevBin = 1;
        for (int b = 0; b < BandCount; b++)
        {
            double frac = (double)(b + 1) / BandCount;
            int endBin = Math.Clamp((int)Math.Round(Math.Pow(FftSize / 2.0, frac)), prevBin + 1, (FftSize / 2) - 1);
            double maxMag = 0.0;
            for (int bin = prevBin; bin <= endBin; bin++)
            {
                double mag = Math.Sqrt(real[bin] * real[bin] + imag[bin] * imag[bin]) / FftSize;
                if (mag > maxMag) maxMag = mag;
            }
            prevBin = Math.Min(endBin + 1, (FftSize / 2) - 2);

            double dbNorm = Math.Clamp((20.0 * Math.Log10(maxMag + 1e-6) + 58.0) / 48.0, 0.0, 1.0);
            double smoothed = prevSmoothed[b] * 0.55 + dbNorm * 0.45;
            prevSmoothed[b] = smoothed;
            bandsOut[b] = smoothed;
        }
    }
}
