/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2013, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * See COPYING for license terms (Ms-PL).                                   *
 *                                                                          *
 ***************************************************************************/

using System;
using System.Collections.Generic;

namespace NVorbis
{
#if !UNSAFE_MDCT
    class Mdct
    {
        const float M_PI = 3.14159265358979323846264f;

        static Dictionary<int, Mdct> _setupCache = new Dictionary<int, Mdct>(2);

        public static void Reverse(float[] samples, int sampleCount)
        {
            GetSetup(sampleCount).CalcReverse(samples);
        }

        static Mdct GetSetup(int n)
        {
            if (!_setupCache.ContainsKey(n))
            {
                lock (_setupCache)
                {
                    if (!_setupCache.ContainsKey(n))
                    {
                        _setupCache[n] = new Mdct(n);
                    }
                }
            }

            return _setupCache[n];
        }

        int n, n2, n4, n8, ld;

        float[] A, B, C, buf2;
        ushort[] bitrev;

        private Mdct(int n)
        {
            this.n = n;
            n2 = n >> 1;
            n4 = n2 >> 1;
            n8 = n4 >> 1;

            ld = Utils.ilog(n) - 1;

            // first, calc the "twiddle factors"
            A = new float[n2];
            B = new float[n2];
            C = new float[n4];
            buf2 = new float[n2];
            int k, k2;
            for (k = k2 = 0; k < n4; ++k, k2 += 2)
            {
                A[k2] = (float)Math.Cos(4 * k * M_PI / n);
                A[k2 + 1] = (float)-Math.Sin(4 * k * M_PI / n);
                B[k2] = (float)Math.Cos((k2 + 1) * M_PI / n / 2) * .5f;
                B[k2 + 1] = (float)Math.Sin((k2 + 1) * M_PI / n / 2) * .5f;
            }
            for (k = k2 = 0; k < n8; ++k, k2 += 2)
            {
                C[k2] = (float)Math.Cos(2 * (k2 + 1) * M_PI / n);
                C[k2 + 1] = (float)-Math.Sin(2 * (k2 + 1) * M_PI / n);
            }

            // now, calc the bit reverse table
            bitrev = new ushort[n8];
            for (int i = 0; i < n8; ++i)
            {
                bitrev[i] = (ushort)(Utils.BitReverse((uint)i, ld - 3) << 2);
            }
        }

        void CalcReverse(float[] buffer)
        {
            float[] u, v;

            // copy and reflect spectral data
            // step 0

            {
                var d = n2 - 2; // buf2
                var AA = 0;     // A
                var e = 0;      // buffer
                var e_stop = n2;// buffer
                while (e != e_stop)
                {
                    buf2[d + 1] = (buffer[e] * A[AA] - buffer[e + 2] * A[AA + 1]);
                    buf2[d] = (buffer[e] * A[AA + 1] + buffer[e + 2] * A[AA]);
                    d -= 2;
                    AA += 2;
                    e += 4;
                }

                e = n2 - 3;
                while (d >= 0)
                {
                    buf2[d + 1] = (-buffer[e + 2] * A[AA] - -buffer[e] * A[AA + 1]);
                    buf2[d] = (-buffer[e + 2] * A[AA + 1] + -buffer[e] * A[AA]);
                    d -= 2;
                    AA += 2;
                    e -= 4;
                }
            }

            // apply "symbolic" names
            u = buffer;
            v = buf2;

            // step 2

            {
                var AA = n2 - 8;    // A

                var e0 = n4;        // v
                var e1 = 0;         // v

                var d0 = n4;        // u
                var d1 = 0;         // u

                while (AA >= 0)
                {
                    float v40_20, v41_21;

                    v41_21 = v[e0 + 1] - v[e1 + 1];
                    v40_20 = v[e0] - v[e1];
                    u[d0 + 1] = v[e0 + 1] + v[e1 + 1];
                    u[d0] = v[e0] + v[e1];
                    u[d1 + 1] = v41_21 * A[AA + 4] - v40_20 * A[AA + 5];
                    u[d1] = v40_20 * A[AA + 4] + v41_21 * A[AA + 5];

                    v41_21 = v[e0 + 3] - v[e1 + 3];
                    v40_20 = v[e0 + 2] - v[e1 + 2];
                    u[d0 + 3] = v[e0 + 3] + v[e1 + 3];
                    u[d0 + 2] = v[e0 + 2] + v[e1 + 2];
                    u[d1 + 3] = v41_21 * A[AA] - v40_20 * A[AA + 1];
                    u[d1 + 2] = v40_20 * A[AA] + v41_21 * A[AA + 1];

                    AA -= 8;

                    d0 += 4;
                    d1 += 4;
                    e0 += 4;
                    e1 += 4;
                }
            }

            // step 3

            // iteration 0
            step3_iter0_loop(n >> 4, u, n2 - 1 - n4 * 0, -n8);
            step3_iter0_loop(n >> 4, u, n2 - 1 - n4 * 1, -n8);

            // iteration 1
            step3_inner_r_loop(n >> 5, u, n2 - 1 - n8 * 0, -(n >> 4), 16);
            step3_inner_r_loop(n >> 5, u, n2 - 1 - n8 * 1, -(n >> 4), 16);
            step3_inner_r_loop(n >> 5, u, n2 - 1 - n8 * 2, -(n >> 4), 16);
            step3_inner_r_loop(n >> 5, u, n2 - 1 - n8 * 3, -(n >> 4), 16);

            // iterations 2 ... x
            var l = 2;
            for (; l < (ld - 3) >> 1; ++l)
            {
                var k0 = n >> (l + 2);
                var k0_2 = k0 >> 1;
                var lim = 1 << (l + 1);
                for (int i = 0; i < lim; ++i)
                {
                    step3_inner_r_loop(n >> (l + 4), u, n2 - 1 - k0 * i, -k0_2, 1 << (l + 3));
                }
            }

            // iterations x ... end
            for (; l < ld - 6; ++l)
            {
                var k0 = n >> (l + 2);
                var k1 = 1 << (l + 3);
                var k0_2 = k0 >> 1;
                var rlim = n >> (l + 6);
                var lim = 1 << l + 1;
                var i_off = n2 - 1;
                var A0 = 0;

                for (int r = rlim; r > 0; --r)
                {
                    step3_inner_s_loop(lim, u, i_off, -k0_2, A0, k1, k0);
                    A0 += k1 * 4;
                    i_off -= 8;
                }
            }

            // combine some iteration steps...
            step3_inner_s_loop_ld654(n >> 5, u, n2 - 1, n);

            // steps 4, 5, and 6
            {
                var bit = 0;

                var d0 = n4 - 4;    // v
                var d1 = n2 - 4;    // v
                while (d0 >= 0)
                {
                    int k4;

                    k4 = bitrev[bit];
                    v[d1 + 3] = u[k4];
                    v[d1 + 2] = u[k4 + 1];
                    v[d0 + 3] = u[k4 + 2];
                    v[d0 + 2] = u[k4 + 3];

                    k4 = bitrev[bit + 1];
                    v[d1 + 1] = u[k4];
                    v[d1] = u[k4 + 1];
                    v[d0 + 1] = u[k4 + 2];
                    v[d0] = u[k4 + 3];

                    d0 -= 4;
                    d1 -= 4;
                    bit += 2;
                }
            }

            // step 7
            {
                var c = 0;      // C
                var d = 0;      // v
                var e = n2 - 4; // v

                while (d < e)
                {
                    float a02, a11, b0, b1, b2, b3;

                    a02 = v[d] - v[e + 2];
                    a11 = v[d + 1] + v[e + 3];

                    b0 = C[c + 1] * a02 + C[c] * a11;
                    b1 = C[c + 1] * a11 - C[c] * a02;

                    b2 = v[d] + v[e + 2];
                    b3 = v[d + 1] - v[e + 3];

                    v[d] = b2 + b0;
                    v[d + 1] = b3 + b1;
                    v[e + 2] = b2 - b0;
                    v[e + 3] = b1 - b3;

                    a02 = v[d + 2] - v[e];
                    a11 = v[d + 3] + v[e + 1];

                    b0 = C[c + 3] * a02 + C[c + 2] * a11;
                    b1 = C[c + 3] * a11 - C[c + 2] * a02;

                    b2 = v[d + 2] + v[e];
                    b3 = v[d + 3] - v[e + 1];

                    v[d + 2] = b2 + b0;
                    v[d + 3] = b3 + b1;
                    v[e] = b2 - b0;
                    v[e + 1] = b1 - b3;

                    c += 4;
                    d += 4;
                    e -= 4;
                }
            }

            // step 8 + decode
            {
                var b = n2 - 8; // B
                var e = n2 - 8; // buf2
                var d0 = 0;     // buffer
                var d1 = n2 - 4;// buffer
                var d2 = n2;    // buffer
                var d3 = n - 4; // buffer
                while (e >= 0)
                {
                    float p0, p1, p2, p3;

                    p3 = buf2[e + 6] * B[b + 7] - buf2[e + 7] * B[b + 6];
                    p2 = -buf2[e + 6] * B[b + 6] - buf2[e + 7] * B[b + 7];

                    buffer[d0] = p3;
                    buffer[d1 + 3] = -p3;
                    buffer[d2] = p2;
                    buffer[d3 + 3] = p2;

                    p1 = buf2[e + 4] * B[b + 5] - buf2[e + 5] * B[b + 4];
                    p0 = -buf2[e + 4] * B[b + 4] - buf2[e + 5] * B[b + 5];

                    buffer[d0 + 1] = p1;
                    buffer[d1 + 2] = -p1;
                    buffer[d2 + 1] = p0;
                    buffer[d3 + 2] = p0;


                    p3 = buf2[e + 2] * B[b + 3] - buf2[e + 3] * B[b + 2];
                    p2 = -buf2[e + 2] * B[b + 2] - buf2[e + 3] * B[b + 3];

                    buffer[d0 + 2] = p3;
                    buffer[d1 + 1] = -p3;
                    buffer[d2 + 2] = p2;
                    buffer[d3 + 1] = p2;

                    p1 = buf2[e] * B[b + 1] - buf2[e + 1] * B[b];
                    p0 = -buf2[e] * B[b] - buf2[e + 1] * B[b + 1];

                    buffer[d0 + 3] = p1;
                    buffer[d1] = -p1;
                    buffer[d2 + 3] = p0;
                    buffer[d3] = p0;

                    b -= 8;
                    e -= 8;
                    d0 += 4;
                    d2 += 4;
                    d1 -= 4;
                    d3 -= 4;
                }
            }
        }

        void step3_iter0_loop(int n, float[] e, int i_off, int k_off)
        {
            var ee0 = i_off;        // e
            var ee2 = ee0 + k_off;  // e
            var a = 0;
            for (int i = n >> 2; i > 0; --i)
            {
                float k00_20, k01_21;

                k00_20 = e[ee0] - e[ee2];
                k01_21 = e[ee0 - 1] - e[ee2 - 1];
                e[ee0] += e[ee2];
                e[ee0 - 1] += e[ee2 - 1];
                e[ee2] = k00_20 * A[a] - k01_21 * A[a + 1];
                e[ee2 - 1] = k01_21 * A[a] + k00_20 * A[a + 1];
                a += 8;

                k00_20 = e[ee0 - 2] - e[ee2 - 2];
                k01_21 = e[ee0 - 3] - e[ee2 - 3];
                e[ee0 - 2] += e[ee2 - 2];
                e[ee0 - 3] += e[ee2 - 3];
                e[ee2 - 2] = k00_20 * A[a] - k01_21 * A[a + 1];
                e[ee2 - 3] = k01_21 * A[a] + k00_20 * A[a + 1];
                a += 8;

                k00_20 = e[ee0 - 4] - e[ee2 - 4];
                k01_21 = e[ee0 - 5] - e[ee2 - 5];
                e[ee0 - 4] += e[ee2 - 4];
                e[ee0 - 5] += e[ee2 - 5];
                e[ee2 - 4] = k00_20 * A[a] - k01_21 * A[a + 1];
                e[ee2 - 5] = k01_21 * A[a] + k00_20 * A[a + 1];
                a += 8;

                k00_20 = e[ee0 - 6] - e[ee2 - 6];
                k01_21 = e[ee0 - 7] - e[ee2 - 7];
                e[ee0 - 6] += e[ee2 - 6];
                e[ee0 - 7] += e[ee2 - 7];
                e[ee2 - 6] = k00_20 * A[a] - k01_21 * A[a + 1];
                e[ee2 - 7] = k01_21 * A[a] + k00_20 * A[a + 1];
                a += 8;

                ee0 -= 8;
                ee2 -= 8;
            }
        }

        void step3_inner_r_loop(int lim, float[] e, int d0, int k_off, int k1)
        {
            float k00_20, k01_21;

            var e0 = d0;            // e
            var e2 = e0 + k_off;    // e
            int a = 0;

            for (int i = lim >> 2; i > 0; --i)
            {
                k00_20 = e[e0] - e[e2];
                k01_21 = e[e0 - 1] - e[e2 - 1];
                e[e0] += e[e2];
                e[e0 - 1] += e[e2 - 1];
                e[e2] = k00_20 * A[a] - k01_21 * A[a + 1];
                e[e2 - 1] = k01_21 * A[a] + k00_20 * A[a + 1];

                a += k1;

                k00_20 = e[e0 - 2] - e[e2 - 2];
                k01_21 = e[e0 - 3] - e[e2 - 3];
                e[e0 - 2] += e[e2 - 2];
                e[e0 - 3] += e[e2 - 3];
                e[e2 - 2] = k00_20 * A[a] - k01_21 * A[a + 1];
                e[e2 - 3] = k01_21 * A[a] + k00_20 * A[a + 1];

                a += k1;

                k00_20 = e[e0 - 4] - e[e2 - 4];
                k01_21 = e[e0 - 5] - e[e2 - 5];
                e[e0 - 4] += e[e2 - 4];
                e[e0 - 5] += e[e2 - 5];
                e[e2 - 4] = k00_20 * A[a] - k01_21 * A[a + 1];
                e[e2 - 5] = k01_21 * A[a] + k00_20 * A[a + 1];

                a += k1;

                k00_20 = e[e0 - 6] - e[e2 - 6];
                k01_21 = e[e0 - 7] - e[e2 - 7];
                e[e0 - 6] += e[e2 - 6];
                e[e0 - 7] += e[e2 - 7];
                e[e2 - 6] = k00_20 * A[a] - k01_21 * A[a + 1];
                e[e2 - 7] = k01_21 * A[a] + k00_20 * A[a + 1];

                a += k1;

                e0 -= 8;
                e2 -= 8;
            }
        }

        void step3_inner_s_loop(int n, float[] e, int i_off, int k_off, int a, int a_off, int k0)
        {
            var A0 = A[a];
            var A1 = A[a + 1];
            var A2 = A[a + a_off];
            var A3 = A[a + a_off + 1];
            var A4 = A[a + a_off * 2];
            var A5 = A[a + a_off * 2 + 1];
            var A6 = A[a + a_off * 3];
            var A7 = A[a + a_off * 3 + 1];

            float k00, k11;

            var ee0 = i_off;        // e
            var ee2 = ee0 + k_off;  // e

            for (int i = n; i > 0; --i)
            {
                k00 = e[ee0] - e[ee2];
                k11 = e[ee0 - 1] - e[ee2 - 1];
                e[ee0] += e[ee2];
                e[ee0 - 1] += e[ee2 - 1];
                e[ee2] = k00 * A0 - k11 * A1;
                e[ee2 - 1] = k11 * A0 + k00 * A1;

                k00 = e[ee0 - 2] - e[ee2 - 2];
                k11 = e[ee0 - 3] - e[ee2 - 3];
                e[ee0 - 2] += e[ee2 - 2];
                e[ee0 - 3] += e[ee2 - 3];
                e[ee2 - 2] = k00 * A2 - k11 * A3;
                e[ee2 - 3] = k11 * A2 + k00 * A3;

                k00 = e[ee0 - 4] - e[ee2 - 4];
                k11 = e[ee0 - 5] - e[ee2 - 5];
                e[ee0 - 4] += e[ee2 - 4];
                e[ee0 - 5] += e[ee2 - 5];
                e[ee2 - 4] = k00 * A4 - k11 * A5;
                e[ee2 - 5] = k11 * A4 + k00 * A5;

                k00 = e[ee0 - 6] - e[ee2 - 6];
                k11 = e[ee0 - 7] - e[ee2 - 7];
                e[ee0 - 6] += e[ee2 - 6];
                e[ee0 - 7] += e[ee2 - 7];
                e[ee2 - 6] = k00 * A6 - k11 * A7;
                e[ee2 - 7] = k11 * A6 + k00 * A7;

                ee0 -= k0;
                ee2 -= k0;
            }
        }

        void step3_inner_s_loop_ld654(int n, float[] e, int i_off, int base_n)
        {
            var a_off = base_n >> 3;
            var A2 = A[a_off];
            var z = i_off;          // e
            var @base = z - 16 * n; // e

            while (z > @base)
            {
                float k00, k11;

                k00 = e[z] - e[z - 8];
                k11 = e[z - 1] - e[z - 9];
                e[z] += e[z - 8];
                e[z - 1] += e[z - 9];
                e[z - 8] = k00;
                e[z - 9] = k11;

                k00 = e[z - 2] - e[z - 10];
                k11 = e[z - 3] - e[z - 11];
                e[z - 2] += e[z - 10];
                e[z - 3] += e[z - 11];
                e[z - 10] = (k00 + k11) * A2;
                e[z - 11] = (k11 - k00) * A2;

                k00 = e[z - 12] - e[z - 4];
                k11 = e[z - 5] - e[z - 13];
                e[z - 4] += e[z - 12];
                e[z - 5] += e[z - 13];
                e[z - 12] = k11;
                e[z - 13] = k00;

                k00 = e[z - 14] - e[z - 6];
                k11 = e[z - 7] - e[z - 15];
                e[z - 6] += e[z - 14];
                e[z - 7] += e[z - 15];
                e[z - 14] = (k00 + k11) * A2;
                e[z - 15] = (k00 - k11) * A2;

                iter_54(e, z);
                iter_54(e, z - 8);

                z -= 16;
            }
        }

        private void iter_54(float[] e, int z)
        {
            float k00, k11, k22, k33;
            float y0, y1, y2, y3;

            k00 = e[z] - e[z - 4];
            y0 = e[z] + e[z - 4];
            y2 = e[z - 2] + e[z - 6];
            k22 = e[z - 2] - e[z - 6];

            e[z] = y0 + y2;
            e[z - 2] = y0 - y2;

            k33 = e[z - 3] - e[z - 7];

            e[z - 4] = k00 + k33;
            e[z - 6] = k00 - k33;

            k11 = e[z - 1] - e[z - 5];
            y1 = e[z - 1] + e[z - 5];
            y3 = e[z - 3] + e[z - 7];

            e[z - 1] = y1 + y3;
            e[z - 3] = y1 - y3;
            e[z - 5] = k11 - k22;
            e[z - 7] = k11 + k22;
        }
    }
#else
    class Mdct
    {
        const float M_PI = 3.14159265358979323846264f;

        static Dictionary<int, Mdct> _setupCache = new Dictionary<int, Mdct>(2);

        public static void Reverse(float[] samples, int sampleCount)
        {
            GetSetup(sampleCount).CalcReverse(samples);
        }

        static Mdct GetSetup(int n)
        {
            if (!_setupCache.ContainsKey(n))
            {
                lock (_setupCache)
                {
                    if (!_setupCache.ContainsKey(n))
                    {
                        _setupCache[n] = new Mdct(n);
                    }
                }
            }

            return _setupCache[n];
        }

        int n, n2, n4, n8, ld;

        float[] A, B, C, buf2Temp;
        ushort[] bitrev;

        private Mdct(int n)
        {
            this.n = n;
            n2 = n >> 1;
            n4 = n2 >> 1;
            n8 = n4 >> 1;

            ld = Utils.ilog(n) - 1;

            // first, calc the "twiddle factors"
            A = new float[n2];
            B = new float[n2];
            C = new float[n4];
            buf2Temp = new float[n2];
            int k, k2;
            for (k = k2 = 0; k < n4; ++k, k2 += 2)
            {
                A[k2    ] = (float) Math.Cos(4 * k * M_PI / n);
                A[k2 + 1] = (float)-Math.Sin(4 * k * M_PI / n);
                B[k2    ] = (float) Math.Cos((k2 + 1) * M_PI / n / 2) * .5f;
                B[k2 + 1] = (float) Math.Sin((k2 + 1) * M_PI / n / 2) * .5f;
            }
            for (k = k2 = 0; k < n8; ++k, k2 += 2)
            {
                C[k2    ] = (float) Math.Cos(2 * (k2 + 1) * M_PI / n);
                C[k2 + 1] = (float)-Math.Sin(2 * (k2 + 1) * M_PI / n);
            }

            // now, calc the bit reverse table
            bitrev = new ushort[n8];
            for (int i = 0; i < n8; ++i)
            {
                //bitrev[i] = (ushort)((Utils.BitReverse((uint)i) >> (32 - ld + 3)) << 2);
                bitrev[i] = (ushort)(Utils.BitReverse((uint)i, ld - 3) << 2);
            }
        }

        unsafe void CalcReverse(float[] buf)
        {
            // we can get away with a lot of fixed statements here since no allocations happen after this line...
            fixed (float* buffer = buf)
            fixed (float* buf2 = buf2Temp)
            {
                float* u, v;

                fixed (float* A = this.A)
                {
                    {
                        float* d, e, AA, e_stop;
                        d = &buf2[n2 - 2];
                        AA = A;
                        e = &buffer[0];
                        e_stop = &buffer[n2];
                        while (e != e_stop)
                        {
                            d[1] = (e[0] * AA[0] - e[2] * AA[1]);
                            d[0] = (e[0] * AA[1] + e[2] * AA[0]);
                            d -= 2;
                            AA += 2;
                            e += 4;
                        }

                        e = &buffer[n2 - 3];
                        while (d >= buf2)
                        {
                            d[1] = (-e[2] * AA[0] - -e[0] * AA[1]);
                            d[0] = (-e[2] * AA[1] + -e[0] * AA[0]);
                            d -= 2;
                            AA += 2;
                            e -= 4;
                        }
                    }

                    u = buffer;
                    v = buf2;

                    {
                        float* AA = &A[n2 - 8];
                        float* d0, d1, e0, e1;

                        e0 = &v[n4];
                        e1 = &v[0];

                        d0 = &u[n4];
                        d1 = &u[0];

                        while (AA >= A)
                        {
                            float v40_20, v41_21;

                            v41_21 = e0[1] - e1[1];
                            v40_20 = e0[0] - e1[0];
                            d0[1] = e0[1] + e1[1];
                            d0[0] = e0[0] + e1[0];
                            d1[1] = v41_21 * AA[4] - v40_20 * AA[5];
                            d1[0] = v40_20 * AA[4] + v41_21 * AA[5];

                            v41_21 = e0[3] - e1[3];
                            v40_20 = e0[2] - e1[2];
                            d0[3] = e0[3] + e1[3];
                            d0[2] = e0[2] + e1[2];
                            d1[3] = v41_21 * AA[0] - v40_20 * AA[1];
                            d1[2] = v40_20 * AA[0] + v41_21 * AA[1];

                            AA -= 8;

                            d0 += 4;
                            d1 += 4;
                            e0 += 4;
                            e1 += 4;
                        }
                    }

                    imdct_step3_iter0_loop(n >> 4, u, n2 - 1 - n4 * 0, -(n >> 3), A);
                    imdct_step3_iter0_loop(n >> 4, u, n2 - 1 - n4 * 1, -(n >> 3), A);

                    imdct_step3_inner_r_loop(n >> 5, u, n2 - 1 - n8 * 0, -(n >> 4), A, 16);
                    imdct_step3_inner_r_loop(n >> 5, u, n2 - 1 - n8 * 1, -(n >> 4), A, 16);
                    imdct_step3_inner_r_loop(n >> 5, u, n2 - 1 - n8 * 2, -(n >> 4), A, 16);
                    imdct_step3_inner_r_loop(n >> 5, u, n2 - 1 - n8 * 3, -(n >> 4), A, 16);

                    var l = 2;
                    for (; l < (ld - 3) >> 1; ++l)
                    {
                        int k0 = n >> (l + 2), k0_2 = k0 >> 1;
                        int lim = 1 << (l + 1);
                        int i;
                        for (i = 0; i < lim; ++i)
                            imdct_step3_inner_r_loop(n >> (l + 4), u, n2 - 1 - k0 * i, -k0_2, A, 1 << (l + 3));
                    }

                    for (; l < ld - 6; ++l)
                    {
                        int k0 = n >> (l + 2), k1 = 1 << (l + 3), k0_2 = k0 >> 1;
                        int rlim = n >> (l + 6), r;
                        int lim = 1 << (l + 1);
                        int i_off;
                        float* A0 = A;
                        i_off = n2 - 1;
                        for (r = rlim; r > 0; --r)
                        {
                            imdct_step3_inner_s_loop(lim, u, i_off, -k0_2, A0, k1, k0);
                            A0 += k1 * 4;
                            i_off -= 8;
                        }
                    }

                    imdct_step3_inner_s_loop_ld654(n >> 5, u, n2 - 1, A, n);
                }

                fixed (ushort* bitrevTemp = this.bitrev)
                {
                    ushort* bitrev = bitrevTemp;

                    float* d0 = &v[n4 - 4];
                    float* d1 = &v[n2 - 4];
                    while (d0 >= v)
                    {
                        int k4;

                        k4 = bitrev[0];
                        d1[3] = u[k4 + 0];
                        d1[2] = u[k4 + 1];
                        d0[3] = u[k4 + 2];
                        d0[2] = u[k4 + 3];

                        k4 = bitrev[1];
                        d1[1] = u[k4 + 0];
                        d1[0] = u[k4 + 1];
                        d0[1] = u[k4 + 2];
                        d0[0] = u[k4 + 3];

                        d0 -= 4;
                        d1 -= 4;
                        bitrev += 2;
                    }
                }

                fixed (float* CTemp = this.C)
                {
                    float* C = CTemp;

                    float* d = v;
                    float* e = v + n2 - 4;

                    while (d < e)
                    {
                        float a02, a11, b0, b1, b2, b3;

                        a02 = d[0] - e[2];
                        a11 = d[1] + e[3];

                        b0 = C[1] * a02 + C[0] * a11;
                        b1 = C[1] * a11 - C[0] * a02;

                        b2 = d[0] + e[2];
                        b3 = d[1] - e[3];

                        d[0] = b2 + b0;
                        d[1] = b3 + b1;
                        e[2] = b2 - b0;
                        e[3] = b1 - b3;

                        a02 = d[2] - e[0];
                        a11 = d[3] + e[1];

                        b0 = C[3] * a02 + C[2] * a11;
                        b1 = C[3] * a11 - C[2] * a02;

                        b2 = d[2] + e[0];
                        b3 = d[3] - e[1];

                        d[2] = b2 + b0;
                        d[3] = b3 + b1;
                        e[0] = b2 - b0;
                        e[1] = b1 - b3;

                        C += 4;
                        d += 4;
                        e -= 4;
                    }
                }

                fixed (float* BTemp = this.B)
                {
                    float* d0, d1, d2, d3;
                    float* B = BTemp + n2 - 8;
                    float* e = buf2 + n2 - 8;
                    d0 = &buffer[0];
                    d1 = &buffer[n2 - 4];
                    d2 = &buffer[n2];
                    d3 = &buffer[n - 4];
                    while (e >= v)
                    {
                        float p0, p1, p2, p3;

                        p3 = e[6] * B[7] - e[7] * B[6];
                        p2 = -e[6] * B[6] - e[7] * B[7];

                        d0[0] = p3;
                        d1[3] = -p3;
                        d2[0] = p2;
                        d3[3] = p2;

                        p1 = e[4] * B[5] - e[5] * B[4];
                        p0 = -e[4] * B[4] - e[5] * B[5];

                        d0[1] = p1;
                        d1[2] = -p1;
                        d2[1] = p0;
                        d3[2] = p0;

                        p3 = e[2] * B[3] - e[3] * B[2];
                        p2 = -e[2] * B[2] - e[3] * B[3];

                        d0[2] = p3;
                        d1[1] = -p3;
                        d2[2] = p2;
                        d3[1] = p2;

                        p1 = e[0] * B[1] - e[1] * B[0];
                        p0 = -e[0] * B[0] - e[1] * B[1];

                        d0[3] = p1;
                        d1[0] = -p1;
                        d2[3] = p0;
                        d3[0] = p0;

                        B -= 8;
                        e -= 8;
                        d0 += 4;
                        d2 += 4;
                        d1 -= 4;
                        d3 -= 4;
                    }
                }
            }
        }

        unsafe void imdct_step3_iter0_loop(int n, float* e, int i_off, int k_off, float* A)
        {
            float* ee0 = e + i_off;
            float* ee2 = ee0 + k_off;
            int i;

            for (i = (n >> 2); i > 0; --i)
            {
                float k00_20, k01_21;
                k00_20 = ee0[0] - ee2[0];
                k01_21 = ee0[-1] - ee2[-1];
                ee0[0] += ee2[0];//ee0[ 0] = ee0[ 0] + ee2[ 0];
                ee0[-1] += ee2[-1];//ee0[-1] = ee0[-1] + ee2[-1];
                ee2[0] = k00_20 * A[0] - k01_21 * A[1];
                ee2[-1] = k01_21 * A[0] + k00_20 * A[1];
                A += 8;

                k00_20 = ee0[-2] - ee2[-2];
                k01_21 = ee0[-3] - ee2[-3];
                ee0[-2] += ee2[-2];//ee0[-2] = ee0[-2] + ee2[-2];
                ee0[-3] += ee2[-3];//ee0[-3] = ee0[-3] + ee2[-3];
                ee2[-2] = k00_20 * A[0] - k01_21 * A[1];
                ee2[-3] = k01_21 * A[0] + k00_20 * A[1];
                A += 8;

                k00_20 = ee0[-4] - ee2[-4];
                k01_21 = ee0[-5] - ee2[-5];
                ee0[-4] += ee2[-4];//ee0[-4] = ee0[-4] + ee2[-4];
                ee0[-5] += ee2[-5];//ee0[-5] = ee0[-5] + ee2[-5];
                ee2[-4] = k00_20 * A[0] - k01_21 * A[1];
                ee2[-5] = k01_21 * A[0] + k00_20 * A[1];
                A += 8;

                k00_20 = ee0[-6] - ee2[-6];
                k01_21 = ee0[-7] - ee2[-7];
                ee0[-6] += ee2[-6];//ee0[-6] = ee0[-6] + ee2[-6];
                ee0[-7] += ee2[-7];//ee0[-7] = ee0[-7] + ee2[-7];
                ee2[-6] = k00_20 * A[0] - k01_21 * A[1];
                ee2[-7] = k01_21 * A[0] + k00_20 * A[1];
                A += 8;
                ee0 -= 8;
                ee2 -= 8;
            }
        }

        unsafe void imdct_step3_inner_r_loop(int lim, float* e, int d0, int k_off, float* A, int k1)
        {
            int i;
            float k00_20, k01_21;

            float* e0 = e + d0;
            float* e2 = e0 + k_off;

            for (i = lim >> 2; i > 0; --i)
            {
                k00_20 = e0[-0] - e2[-0];
                k01_21 = e0[-1] - e2[-1];
                e0[-0] += e2[-0];//e0[-0] = e0[-0] + e2[-0];
                e0[-1] += e2[-1];//e0[-1] = e0[-1] + e2[-1];
                e2[-0] = (k00_20) * A[0] - (k01_21) * A[1];
                e2[-1] = (k01_21) * A[0] + (k00_20) * A[1];

                A += k1;

                k00_20 = e0[-2] - e2[-2];
                k01_21 = e0[-3] - e2[-3];
                e0[-2] += e2[-2];//e0[-2] = e0[-2] + e2[-2];
                e0[-3] += e2[-3];//e0[-3] = e0[-3] + e2[-3];
                e2[-2] = (k00_20) * A[0] - (k01_21) * A[1];
                e2[-3] = (k01_21) * A[0] + (k00_20) * A[1];

                A += k1;

                k00_20 = e0[-4] - e2[-4];
                k01_21 = e0[-5] - e2[-5];
                e0[-4] += e2[-4];//e0[-4] = e0[-4] + e2[-4];
                e0[-5] += e2[-5];//e0[-5] = e0[-5] + e2[-5];
                e2[-4] = (k00_20) * A[0] - (k01_21) * A[1];
                e2[-5] = (k01_21) * A[0] + (k00_20) * A[1];

                A += k1;

                k00_20 = e0[-6] - e2[-6];
                k01_21 = e0[-7] - e2[-7];
                e0[-6] += e2[-6];//e0[-6] = e0[-6] + e2[-6];
                e0[-7] += e2[-7];//e0[-7] = e0[-7] + e2[-7];
                e2[-6] = (k00_20) * A[0] - (k01_21) * A[1];
                e2[-7] = (k01_21) * A[0] + (k00_20) * A[1];

                e0 -= 8;
                e2 -= 8;

                A += k1;
            }
        }

        unsafe void imdct_step3_inner_s_loop(int n, float* e, int i_off, int k_off, float* A, int a_off, int k0)
        {
            int i;
            float A0 = A[0];
            float A1 = A[0 + 1];
            float A2 = A[0 + a_off];
            float A3 = A[0 + a_off + 1];
            float A4 = A[0 + a_off * 2 + 0];
            float A5 = A[0 + a_off * 2 + 1];
            float A6 = A[0 + a_off * 3 + 0];
            float A7 = A[0 + a_off * 3 + 1];

            float k00, k11;

            float* ee0 = e + i_off;
            float* ee2 = ee0 + k_off;

            for (i = n; i > 0; --i)
            {
                k00 = ee0[0] - ee2[0];
                k11 = ee0[-1] - ee2[-1];
                ee0[0] = ee0[0] + ee2[0];
                ee0[-1] = ee0[-1] + ee2[-1];
                ee2[0] = (k00) * A0 - (k11) * A1;
                ee2[-1] = (k11) * A0 + (k00) * A1;

                k00 = ee0[-2] - ee2[-2];
                k11 = ee0[-3] - ee2[-3];
                ee0[-2] = ee0[-2] + ee2[-2];
                ee0[-3] = ee0[-3] + ee2[-3];
                ee2[-2] = (k00) * A2 - (k11) * A3;
                ee2[-3] = (k11) * A2 + (k00) * A3;

                k00 = ee0[-4] - ee2[-4];
                k11 = ee0[-5] - ee2[-5];
                ee0[-4] = ee0[-4] + ee2[-4];
                ee0[-5] = ee0[-5] + ee2[-5];
                ee2[-4] = (k00) * A4 - (k11) * A5;
                ee2[-5] = (k11) * A4 + (k00) * A5;

                k00 = ee0[-6] - ee2[-6];
                k11 = ee0[-7] - ee2[-7];
                ee0[-6] = ee0[-6] + ee2[-6];
                ee0[-7] = ee0[-7] + ee2[-7];
                ee2[-6] = (k00) * A6 - (k11) * A7;
                ee2[-7] = (k11) * A6 + (k00) * A7;

                ee0 -= k0;
                ee2 -= k0;
            }
        }

        unsafe void imdct_step3_inner_s_loop_ld654(int n, float* e, int i_off, float* A, int base_n)
        {
            int a_off = base_n >> 3;
            float A2 = A[0 + a_off];
            float* z = e + i_off;
            float* @base = z - 16 * n;

            while (z > @base)
            {
                float k00, k11;

                k00 = z[-0] - z[-8];
                k11 = z[-1] - z[-9];
                z[-0] = z[-0] + z[-8];
                z[-1] = z[-1] + z[-9];
                z[-8] = k00;
                z[-9] = k11;

                k00 = z[-2] - z[-10];
                k11 = z[-3] - z[-11];
                z[-2] = z[-2] + z[-10];
                z[-3] = z[-3] + z[-11];
                z[-10] = (k00 + k11) * A2;
                z[-11] = (k11 - k00) * A2;

                k00 = z[-12] - z[-4];  // reverse to avoid a unary negation
                k11 = z[-5] - z[-13];
                z[-4] = z[-4] + z[-12];
                z[-5] = z[-5] + z[-13];
                z[-12] = k11;
                z[-13] = k00;

                k00 = z[-14] - z[-6];  // reverse to avoid a unary negation
                k11 = z[-7] - z[-15];
                z[-6] = z[-6] + z[-14];
                z[-7] = z[-7] + z[-15];
                z[-14] = (k00 + k11) * A2;
                z[-15] = (k00 - k11) * A2;

                iter_54(z);
                iter_54(z - 8);
                z -= 16;
            }
        }

        unsafe void iter_54(float* z)
        {
            float k00, k11, k22, k33;
            float y0, y1, y2, y3;

            k00 = z[0] - z[-4];
            y0 = z[0] + z[-4];
            y2 = z[-2] + z[-6];
            k22 = z[-2] - z[-6];

            z[-0] = y0 + y2;      // z0 + z4 + z2 + z6
            z[-2] = y0 - y2;      // z0 + z4 - z2 - z6

            // done with y0,y2

            k33 = z[-3] - z[-7];

            z[-4] = k00 + k33;    // z0 - z4 + z3 - z7
            z[-6] = k00 - k33;    // z0 - z4 - z3 + z7

            // done with k33

            k11 = z[-1] - z[-5];
            y1 = z[-1] + z[-5];
            y3 = z[-3] + z[-7];

            z[-1] = y1 + y3;      // z1 + z5 + z3 + z7
            z[-3] = y1 - y3;      // z1 + z5 - z3 - z7
            z[-5] = k11 - k22;    // z1 - z5 + z2 - z6
            z[-7] = k11 + k22;    // z1 - z5 - z2 + z6
        }
    }
#endif
}
