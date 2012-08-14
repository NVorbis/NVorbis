/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2012, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * See COPYING for license terms (Ms-PL).                                   *
 *                                                                          *
 ***************************************************************************/
namespace NVorbis
{
    static class Utils
    {
        static internal int ilog(int x)
        {
            int cnt = 0;
            while (x > 0)
            {
                ++cnt;
                x >>= 1;    // this is safe because we'll never get here if the sign bit is set
            }
            return cnt;
        }

        static internal uint BitReverse(uint n)
        {
            return BitReverse(n, 32);
        }

        static internal uint BitReverse(uint n, int bits)
        {
            n = ((n & 0xAAAAAAAA) >> 1) | ((n & 0x55555555) << 1);
            n = ((n & 0xCCCCCCCC) >> 2) | ((n & 0x33333333) << 2);
            n = ((n & 0xF0F0F0F0) >> 4) | ((n & 0x0F0F0F0F) << 4);
            n = ((n & 0xFF00FF00) >> 8) | ((n & 0x00FF00FF) << 8);
            return ((n >> 16) | (n << 16)) >> (32 - bits);
        }

        // make is so we can twiddle bits in a float...
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        struct FloatBits
        {
            [System.Runtime.InteropServices.FieldOffset(0)]
            public float Float;
            [System.Runtime.InteropServices.FieldOffset(0)]
            public uint Bits;
        }

        // clamp the value to the given range. set clipped as appropriate.
        // clipped is an int to make the math as fast as possible...
        static internal float ClipValue(float val, float max, float min, ref int clipped)
        {
            FloatBits fb;
            fb.Bits = 0;
            fb.Float = val - max;

            // if x >= a, the sign bit will be cleared...  If not, it will be set
            var sign = (fb.Bits >> 31) & 1;
            clipped |= (int)sign & 1;
            fb.Float = min - ((max * sign) + (val * (sign ^ 1)));

            // if x <= b, the sign bit will be cleared...  If not, it will be set
            sign = (fb.Bits >> 31) & 1;
            clipped |= (int)sign & 1;
            return (min * sign) + (val * (sign ^ 1));
        }

        static internal float ConvertFromVorbisFloat32(uint bits)
        {
            // do as much as possible with bit tricks
            var sign = ((int)bits >> 31);   // sign-extend to the full 32-bits
            var exponent = (double)((int)((bits & 0x7fe00000) >> 21) - 788);  // grab the exponent, remove the bias, store as double (for the call to System.Math.Pow(...))
            var mantissa = (float)(((bits & 0x1fffff) ^ sign) + (sign & 1));  // grab the mantissa and apply the sign bit.  store as float

            // NB: We could use bit tricks to calc the exponent, but it can't be more than 63 in either direction.
            //     This creates an issue, since the exponent field allows for a *lot* more than that.
            //     On the flip side, larger exponent values don't seem to be used by the Vorbis codebooks...
            //     Either way, we'll play it safe and let the BCL calculate it.

            // now switch to single-precision and calc the return value
            return mantissa * (float)System.Math.Pow(2.0, exponent);
        }
    }
}
