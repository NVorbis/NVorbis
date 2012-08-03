/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2012, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * This MDCT algorithm was copied from csvorbis, a C# port of Jorbis.       *
 * JOrbis is a Java port of libvorbis.                                      *
 *                                                                          *
 * csvorbis was written by Mark Crichton <crichton@gimp.org>.               *
 * JOrbis was written by the JOrbis team.                                   *
 * libvorbis is Copyright Xiph.org Foundation.                              *
 *                                                                          *
 * Original code written by ymnk <ymnk@jcraft.com> in 2000, and is          *
 * Copyright (C) 2000 ymnk, JCraft, Inc.                                    *
 *                                                                          *
 * See COPYING for license terms (LGPL all the way back to libvorbis).      *
 *                                                                          *
 ***************************************************************************/

using System;
using System.Collections.Generic;

namespace NVorbis
{
    class Mdct
    {
        static Dictionary<int, Mdct> _setupCache = new Dictionary<int, Mdct>(2);

        public static float[] Reverse(float[] samples)
        {
            var setup = GetSetup(samples.Length);
            setup.CalcReverse(samples);
            return samples;
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

        int n, n2, n4, n8;
        int log2n;

        float[] trig;
        int[] bitreverse;

        private Mdct(int n)
        {
            this.n = n;
            this.n2 = this.n >> 1;
            this.n4 = this.n2 >> 1;
            this.n8 = this.n4 >> 1;

            this.log2n = (int)Math.Round(Math.Log(n, 2));

            bitreverse = new int[n4];
            trig = new float[n + n4];

            var AE = 0;
            var AO = 1;
            var BE = AE + n2;
            var BO = BE + 1;
            var CE = BE + n2;
            var CO = CE + 1;
            for (int i = 0; i < n4; i++)
            {
                trig[AE + i * 2] = (float)( Math.Cos((Math.PI / n) * (4 * i)));
                trig[AO + i * 2] = (float)(-Math.Sin((Math.PI / n) * (4 * i)));
                trig[BE + i * 2] = (float)( Math.Cos((Math.PI / (2 * n)) * (2 * i + 1)));
                trig[BO + i * 2] = (float)( Math.Sin((Math.PI / (2 * n)) * (2 * i + 1)));
            }
            for (int i = 0; i < n8; i++)
            {
                trig[CE + i * 2] = (float)( Math.Cos((Math.PI / n) * (4 * i + 2)));
                trig[CO + i * 2] = (float)(-Math.Sin((Math.PI / n) * (4 * i + 2)));
            }

            var mask = (1 << (log2n - 1)) - 1;
            var msb = 1 << (log2n - 2);
            for (var i = 0; i < n8; i++)
            {
                var acc = 0;
                for (var j = 0; (msb >> j) != 0; j++)
                {
                    if (((msb >> j) & i) != 0) acc |= 1 << j;
                }
                bitreverse[i * 2] = ((~acc) & mask);
                bitreverse[i * 2 + 1] = acc;
            }
        }

        void CalcReverse(float[] buffer)
        {
            var x = ACache.Get<float>(n / 2, false);

            // prep conv buffer
            var inO = 1;
            var xO = 0;
            var A = n2;

            for (int i = 0; i < n8; i++)
            {
                A -= 2;
                var inO0 = buffer[inO];
                var inO2 = buffer[inO + 2];
                var A0 = trig[A];
                var A1 = trig[A + 1];
                x[xO++] = -inO2 * A1 - inO0 * A0;
                x[xO++] =  inO0 * A1 - inO2 * A0;
                inO += 4;
            }

            inO = n2 - 4;

            for (int i = 0; i < n8; i++)
            {
                A -= 2;
                var inO0 = buffer[inO];
                var inO2 = buffer[inO + 2];
                var A0 = trig[A];
                var A1 = trig[A + 1];
                x[xO++] = inO0 * A1 + inO2 * A0;
                x[xO++] = inO0 * A0 - inO2 * A1;
                inO -= 4;
            }

            // convolution
            CalcCore(ref x);

            // populate sample buffer (we're reusing the input parameter here for speed)
            var xx = 0;
            var B = n2;
            var o1 = n4;
            var o2 = o1 - 1;
            var o3 = n4 + n2;
            var o4 = o3 - 1;

            for (int i = 0; i < n4; i++)
            {
                var xx0 = x[xx];
                var xx1 = x[xx + 1];
                var B0 = trig[B];
                var B1 = trig[B + 1];

                var temp1 =  (xx0 * B1 - xx1 * B0);
                var temp2 = -(xx0 * B0 + xx1 * B1);

                buffer[o1] = -temp1;
                buffer[o2] =  temp1;
                buffer[o3] =  temp2;
                buffer[o4] =  temp2;

                o1++;
                o2--;
                o3++;
                o4--;
                xx += 2;
                B += 2;
            }

            ACache.Return(ref x);
        }

        void CalcCore(ref float[] x)
        {
            var w = ACache.Get<float>(n / 2, false);

            // step 1
            var xA = n4;
            var xB = 0;
            var w2 = n4;
            var A = n2;
            for (int i = 0; i < n4;)
            {
                var x0 = x[xA++];
                var x0b = x[xB++];
                w[w2++] = x0 + x0b;
                x0 -= x0b;

                var x1 = x[xA++];
                var x1b = x[xB++];
                w[w2++] = x1 + x1b;
                x1 -= x1b;

                A -= 4;

                var A0 = trig[A];
                var A1 = trig[A + 1];

                w[i++] = x0 * A0 + x1 * A1;
                w[i++] = x1 * A0 - x0 * A1;
            }

            // step 2
            for (int i = 0; i < log2n - 3; i++)
            {
                int k0 = n >> (i + 2);
                int k1 = 1 << (i + 3);
                int wbase = n2 - 2;

                A = 0;

                for (int r = 0; r < k0 >> 2; r++)
                {
                    var w1e = wbase;
                    var w2e = w1e - (k0 >> 1);
                    var AEv = trig[A];
                    var AOv = trig[A + 1];
                    wbase -= 2;

                    int w1o, w2o;
                    float wE, wO, temp;

                    var count = 2 << i;
                    while (--count >= 0)
                    {
                        w1o = w1e + 1;
                        w2o = w2e + 1;

                        wE = w[w1e];
                        temp = w[w2e];
                        x[w1e] = wE + temp;
                        wE -= temp;

                        wO = w[w1o];
                        temp = w[w2o];
                        x[w1o] = wO + temp;
                        wO -= temp;

                        x[w2o] = wO * AEv - wE * AOv;
                        x[w2e] = wE * AEv + wO * AOv;

                        w1e -= k0;
                        w2e -= k0;
                    }

                    A += k1;
                }

                var buf = w;
                w = x;
                x = buf;
            }

            // step 3
            {
                var C = n;
                var bit = 0;
                var x1 = 0;
                var x2 = n2 - 1;

                for (int i = 0; i < n8; i++)
                {
                    var t1 = bitreverse[bit++];
                    int t2 = bitreverse[bit++];

                    // wA = [t1] - [t2 + 1]
                    // wC = [t1] + [t2 + 1]
                    var wA = w[t1];
                    var wC = w[t2 + 1];
                    wA -= wC;
                    wC = 2 * wC + wA;

                    // wB = [t1 - 1] + [t2]
                    // wD = [t1 - 1] - [t2]
                    var wD = w[t1 - 1];   // NB: start backwards!
                    var wB = w[t2];
                    wD -= wB;             // NB: now "reverse" the storage...
                    wB = 2 * wB + wD;

                    var C0 = trig[C++];
                    var wACE = wA * C0;
                    var wBCE = wB * C0;

                    var C1 = trig[C++];
                    var wACO = wA * C1;
                    var wBCO = wB * C1;

                    x[x1++] = (wC + wACO + wBCE) * .5f;
                    x[x2--] = (x[x1++] = (wD + wBCO - wACE) * .5f) - wD;
                    //x[x2--] = (-wD + wBCO - wACE) * .5f;
                    //x[x1++] = ( wD + wBCO - wACE) * .5f;
                    x[x2--] = (wC - wACO - wBCE) * .5f;
                }
            }

            ACache.Return(ref w);
        }
    }
}
