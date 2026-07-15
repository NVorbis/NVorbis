using NVorbis.Contracts;
using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

namespace NVorbis
{
    /// <summary>
    /// Inverse MDCT implementation derived from stb_vorbis.
    /// </summary>
    class Mdct : IMdct
    {
        // The twiddle tables are immutable once built and depend only on the block size,
        // so they are shared process-wide instead of being rebuilt per decoder.
        static readonly ConcurrentDictionary<int, MdctImpl> s_implCache = new ConcurrentDictionary<int, MdctImpl>();

        // A Vorbis stream uses at most two block sizes, so two slots cover every valid
        // stream without taking a lock or hashing on the per-packet path.
        MdctImpl _impl0, _impl1;

        // Defense-in-depth / documented contract for any caller. The primary guard is StreamDecoder's
        // header-level block-size rejection, which catches malformed input at parse time rather than
        // surfacing it mid-decode deep in this call stack; keep both.
        public void Reverse(float[] samples, int sampleCount)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            if (sampleCount < 64 || sampleCount > 8192 || (sampleCount & (sampleCount - 1)) != 0)
                throw new ArgumentOutOfRangeException(nameof(sampleCount), "Must be a power of two between 64 and 8192.");
            if (samples.Length < sampleCount)
                throw new ArgumentException("Buffer must hold at least sampleCount entries.", nameof(samples));

            var impl = _impl0;
            if (impl == null || impl.N != sampleCount)
            {
                impl = _impl1;
                if (impl == null || impl.N != sampleCount)
                {
                    impl = s_implCache.GetOrAdd(sampleCount, n => new MdctImpl(n));
                    if (Interlocked.CompareExchange(ref _impl0, impl, null) != null)
                    {
                        Interlocked.CompareExchange(ref _impl1, impl, null);
                    }
                }
            }
            impl.CalcReverse(samples);
        }

        class MdctImpl
        {
            [ThreadStatic]
            private static float[] _sharedBuf2;

            readonly int _n, _n2, _n4, _n8, _ld;

            readonly float[] _a, _b, _c;
            readonly ushort[] _bitrev;

            internal int N => _n;

            public MdctImpl(int n)
            {
                _n = n;
                _n2 = n >> 1;
                _n4 = _n2 >> 1;
                _n8 = _n4 >> 1;

                _ld = Utils.ilog(n) - 1;

                // first, calc the "twiddle factors"
                _a = new float[_n2];
                _b = new float[_n2];
                _c = new float[_n4];
                int k, k2;
                for (k = k2 = 0; k < _n4; ++k, k2 += 2)
                {
                    _a[k2] = MathF.Cos(4.0f * k * MathF.PI / n);
                    _a[k2 + 1] = -MathF.Sin(4.0f * k * MathF.PI / n);
                    _b[k2] = MathF.Cos((k2 + 1) * MathF.PI / n / 2) * .5f;
                    _b[k2 + 1] = MathF.Sin((k2 + 1) * MathF.PI / n / 2) * .5f;
                }
                for (k = k2 = 0; k < _n8; ++k, k2 += 2)
                {
                    _c[k2] = MathF.Cos(2.0f * (k2 + 1) * MathF.PI / n);
                    _c[k2 + 1] = -MathF.Sin(2.0f * (k2 + 1) * MathF.PI / n);
                }

                // now, calc the bit reverse table
                _bitrev = new ushort[_n8];
                for (int i = 0; i < _n8; ++i)
                {
                    _bitrev[i] = (ushort)(Utils.BitReverse((uint)i, _ld - 3) << 2);
                }
            }

            internal void CalcReverse(float[] buffer)
            {
                float[] u, v;

                float[] buf2 = _sharedBuf2;
                if (buf2 == null || buf2.Length < _n2)
                {
                    _sharedBuf2 = buf2 = new float[_n2];
                }

                // copy and reflect spectral data
                // step 0

                {
                    ref float d = ref Unsafe.Add(ref buf2[0], _n2 - 2);
                    ref float AA = ref _a[0];
                    ref float e = ref buffer[0];
                    for (int i = _n8; i > 0; --i)
                    {
                        Unsafe.Add(ref d, 1) = (e * AA - Unsafe.Add(ref e, 2) * Unsafe.Add(ref AA, 1));
                        d = (e * Unsafe.Add(ref AA, 1) + Unsafe.Add(ref e, 2) * AA);
                        d = ref Unsafe.Add(ref d, -2);
                        AA = ref Unsafe.Add(ref AA, 2);
                        e = ref Unsafe.Add(ref e, 4);
                    }

                    e = ref Unsafe.Add(ref buffer[0], _n2 - 3);
                    for (int i = _n8; i > 0; --i)
                    {
                        Unsafe.Add(ref d, 1) = (-Unsafe.Add(ref e, 2) * AA - -e * Unsafe.Add(ref AA, 1));
                        d = (-Unsafe.Add(ref e, 2) * Unsafe.Add(ref AA, 1) + -e * AA);
                        d = ref Unsafe.Add(ref d, -2);
                        AA = ref Unsafe.Add(ref AA, 2);
                        e = ref Unsafe.Add(ref e, -4);
                    }
                }

                // apply "symbolic" names
                u = buffer;
                v = buf2;

                // step 2

                {
                    ref float AA = ref Unsafe.Add(ref _a[0], _n2 - 8);

                    ref float e0 = ref Unsafe.Add(ref v[0], _n4);
                    ref float e1 = ref v[0];

                    ref float d0 = ref Unsafe.Add(ref u[0], _n4);
                    ref float d1 = ref u[0];

                    for (int i = _n2 >> 3; i > 0; --i)
                    {
                        float v40_20, v41_21;

                        v41_21 = Unsafe.Add(ref e0, 1) - Unsafe.Add(ref e1, 1);
                        v40_20 = e0 - e1;
                        Unsafe.Add(ref d0, 1) = Unsafe.Add(ref e0, 1) + Unsafe.Add(ref e1, 1);
                        d0 = e0 + e1;
                        Unsafe.Add(ref d1, 1) = v41_21 * Unsafe.Add(ref AA, 4) - v40_20 * Unsafe.Add(ref AA, 5);
                        d1 = v40_20 * Unsafe.Add(ref AA, 4) + v41_21 * Unsafe.Add(ref AA, 5);

                        v41_21 = Unsafe.Add(ref e0, 3) - Unsafe.Add(ref e1, 3);
                        v40_20 = Unsafe.Add(ref e0, 2) - Unsafe.Add(ref e1, 2);
                        Unsafe.Add(ref d0, 3) = Unsafe.Add(ref e0, 3) + Unsafe.Add(ref e1, 3);
                        Unsafe.Add(ref d0, 2) = Unsafe.Add(ref e0, 2) + Unsafe.Add(ref e1, 2);
                        Unsafe.Add(ref d1, 3) = v41_21 * AA - v40_20 * Unsafe.Add(ref AA, 1);
                        Unsafe.Add(ref d1, 2) = v40_20 * AA + v41_21 * Unsafe.Add(ref AA, 1);

                        AA = ref Unsafe.Add(ref AA, -8);

                        d0 = ref Unsafe.Add(ref d0, 4);
                        d1 = ref Unsafe.Add(ref d1, 4);
                        e0 = ref Unsafe.Add(ref e0, 4);
                        e1 = ref Unsafe.Add(ref e1, 4);
                    }
                }

                // step 3

                // step3_inner_s_loop_ld654 below always processes the final three FFT
                // stages, and step 2 above is the first stage. The fixed iteration 0/1
                // calls cover the stages in between, so they must be skipped for small
                // blocks where they would overlap the final three stages and corrupt
                // the output (bug inherited from upstream stb_vorbis).

                // iteration 0
                if (_n > 64)
                {
                    step3_iter0_loop(_n >> 4, u, _n2 - 1 - _n4 * 0, -_n8);
                    step3_iter0_loop(_n >> 4, u, _n2 - 1 - _n4 * 1, -_n8);
                }

                // iteration 1
                if (_n > 128)
                {
                    step3_inner_r_loop(_n >> 5, u, _n2 - 1 - _n8 * 0, -(_n >> 4), 16);
                    step3_inner_r_loop(_n >> 5, u, _n2 - 1 - _n8 * 1, -(_n >> 4), 16);
                    step3_inner_r_loop(_n >> 5, u, _n2 - 1 - _n8 * 2, -(_n >> 4), 16);
                    step3_inner_r_loop(_n >> 5, u, _n2 - 1 - _n8 * 3, -(_n >> 4), 16);
                }

                // iterations 2 ... x
                var l = 2;
                for (; l < (_ld - 3) >> 1; ++l)
                {
                    var k0 = _n >> (l + 2);
                    var k0_2 = k0 >> 1;
                    var lim = 1 << (l + 1);
                    for (int i = 0; i < lim; ++i)
                    {
                        step3_inner_r_loop(_n >> (l + 4), u, _n2 - 1 - k0 * i, -k0_2, 1 << (l + 3));
                    }
                }

                // iterations x ... end
                for (; l < _ld - 6; ++l)
                {
                    var k0 = _n >> (l + 2);
                    var k1 = 1 << (l + 3);
                    var k0_2 = k0 >> 1;
                    var rlim = _n >> (l + 6);
                    var lim = 1 << l + 1;
                    var i_off = _n2 - 1;
                    var A0 = 0;

                    for (int r = rlim; r > 0; --r)
                    {
                        step3_inner_s_loop(lim, u, i_off, -k0_2, A0, k1, k0);
                        A0 += k1 * 4;
                        i_off -= 8;
                    }
                }

                // combine some iteration steps...
                step3_inner_s_loop_ld654(_n >> 5, u, _n2 - 1, _n);

                // steps 4, 5, and 6
                {
                    ref ushort bit = ref _bitrev[0];
                    ref float ub = ref u[0];

                    ref float d0 = ref Unsafe.Add(ref v[0], _n4 - 4);
                    ref float d1 = ref Unsafe.Add(ref v[0], _n2 - 4);
                    for (int i = _n4 >> 2; i > 0; --i)
                    {
                        ref float k4 = ref Unsafe.Add(ref ub, bit);
                        Unsafe.Add(ref d1, 3) = k4;
                        Unsafe.Add(ref d1, 2) = Unsafe.Add(ref k4, 1);
                        Unsafe.Add(ref d0, 3) = Unsafe.Add(ref k4, 2);
                        Unsafe.Add(ref d0, 2) = Unsafe.Add(ref k4, 3);

                        k4 = ref Unsafe.Add(ref ub, Unsafe.Add(ref bit, 1));
                        Unsafe.Add(ref d1, 1) = k4;
                        d1 = Unsafe.Add(ref k4, 1);
                        Unsafe.Add(ref d0, 1) = Unsafe.Add(ref k4, 2);
                        d0 = Unsafe.Add(ref k4, 3);

                        d0 = ref Unsafe.Add(ref d0, -4);
                        d1 = ref Unsafe.Add(ref d1, -4);
                        bit = ref Unsafe.Add(ref bit, 2);
                    }
                }

                // step 7
                {
                    ref float c = ref _c[0];
                    ref float d = ref v[0];
                    ref float e = ref Unsafe.Add(ref v[0], _n2 - 4);

                    for (int i = _n2 >> 3; i > 0; --i)
                    {
                        float a02, a11, b0, b1, b2, b3;

                        a02 = d - Unsafe.Add(ref e, 2);
                        a11 = Unsafe.Add(ref d, 1) + Unsafe.Add(ref e, 3);

                        b0 = Unsafe.Add(ref c, 1) * a02 + c * a11;
                        b1 = Unsafe.Add(ref c, 1) * a11 - c * a02;

                        b2 = d + Unsafe.Add(ref e, 2);
                        b3 = Unsafe.Add(ref d, 1) - Unsafe.Add(ref e, 3);

                        d = b2 + b0;
                        Unsafe.Add(ref d, 1) = b3 + b1;
                        Unsafe.Add(ref e, 2) = b2 - b0;
                        Unsafe.Add(ref e, 3) = b1 - b3;

                        a02 = Unsafe.Add(ref d, 2) - e;
                        a11 = Unsafe.Add(ref d, 3) + Unsafe.Add(ref e, 1);

                        b0 = Unsafe.Add(ref c, 3) * a02 + Unsafe.Add(ref c, 2) * a11;
                        b1 = Unsafe.Add(ref c, 3) * a11 - Unsafe.Add(ref c, 2) * a02;

                        b2 = Unsafe.Add(ref d, 2) + e;
                        b3 = Unsafe.Add(ref d, 3) - Unsafe.Add(ref e, 1);

                        Unsafe.Add(ref d, 2) = b2 + b0;
                        Unsafe.Add(ref d, 3) = b3 + b1;
                        e = b2 - b0;
                        Unsafe.Add(ref e, 1) = b1 - b3;

                        c = ref Unsafe.Add(ref c, 4);
                        d = ref Unsafe.Add(ref d, 4);
                        e = ref Unsafe.Add(ref e, -4);
                    }
                }

                // step 8 + decode
                {
                    ref float b = ref Unsafe.Add(ref _b[0], _n2 - 8);
                    ref float e = ref Unsafe.Add(ref buf2[0], _n2 - 8);
                    ref float d0 = ref buffer[0];
                    ref float d1 = ref Unsafe.Add(ref buffer[0], _n2 - 4);
                    ref float d2 = ref Unsafe.Add(ref buffer[0], _n2);
                    ref float d3 = ref Unsafe.Add(ref buffer[0], _n - 4);
                    for (int i = _n2 >> 3; i > 0; --i)
                    {
                        float p0, p1, p2, p3;

                        p3 = Unsafe.Add(ref e, 6) * Unsafe.Add(ref b, 7) - Unsafe.Add(ref e, 7) * Unsafe.Add(ref b, 6);
                        p2 = -Unsafe.Add(ref e, 6) * Unsafe.Add(ref b, 6) - Unsafe.Add(ref e, 7) * Unsafe.Add(ref b, 7);

                        d0 = p3;
                        Unsafe.Add(ref d1, 3) = -p3;
                        d2 = p2;
                        Unsafe.Add(ref d3, 3) = p2;

                        p1 = Unsafe.Add(ref e, 4) * Unsafe.Add(ref b, 5) - Unsafe.Add(ref e, 5) * Unsafe.Add(ref b, 4);
                        p0 = -Unsafe.Add(ref e, 4) * Unsafe.Add(ref b, 4) - Unsafe.Add(ref e, 5) * Unsafe.Add(ref b, 5);

                        Unsafe.Add(ref d0, 1) = p1;
                        Unsafe.Add(ref d1, 2) = -p1;
                        Unsafe.Add(ref d2, 1) = p0;
                        Unsafe.Add(ref d3, 2) = p0;


                        p3 = Unsafe.Add(ref e, 2) * Unsafe.Add(ref b, 3) - Unsafe.Add(ref e, 3) * Unsafe.Add(ref b, 2);
                        p2 = -Unsafe.Add(ref e, 2) * Unsafe.Add(ref b, 2) - Unsafe.Add(ref e, 3) * Unsafe.Add(ref b, 3);

                        Unsafe.Add(ref d0, 2) = p3;
                        Unsafe.Add(ref d1, 1) = -p3;
                        Unsafe.Add(ref d2, 2) = p2;
                        Unsafe.Add(ref d3, 1) = p2;

                        p1 = e * Unsafe.Add(ref b, 1) - Unsafe.Add(ref e, 1) * b;
                        p0 = -e * b - Unsafe.Add(ref e, 1) * Unsafe.Add(ref b, 1);

                        Unsafe.Add(ref d0, 3) = p1;
                        d1 = -p1;
                        Unsafe.Add(ref d2, 3) = p0;
                        d3 = p0;

                        b = ref Unsafe.Add(ref b, -8);
                        e = ref Unsafe.Add(ref e, -8);
                        d0 = ref Unsafe.Add(ref d0, 4);
                        d2 = ref Unsafe.Add(ref d2, 4);
                        d1 = ref Unsafe.Add(ref d1, -4);
                        d3 = ref Unsafe.Add(ref d3, -4);
                    }
                }
            }

            // The step3 helpers use ref arithmetic instead of array indexing: the
            // decreasing computed indices defeat the JIT's bounds-check elimination,
            // and these loops dominate the transform's cost. Offsets are provably
            // in-range for any legal block size (see the golden-output tests). The k00_20/v41_21-style
            // names only make sense against the stb_vorbis/libvorbis C reference; don't rename to "clean
            // up" without re-deriving the index math from that reference.
            void step3_iter0_loop(int n, float[] e, int i_off, int k_off)
            {
                ref float ee0 = ref Unsafe.Add(ref e[0], i_off);
                ref float ee2 = ref Unsafe.Add(ref ee0, k_off);
                ref float aa = ref _a[0];
                for (int i = n >> 2; i > 0; --i)
                {
                    float k00_20, k01_21;

                    k00_20 = ee0 - ee2;
                    k01_21 = Unsafe.Add(ref ee0, -1) - Unsafe.Add(ref ee2, -1);
                    ee0 += ee2;
                    Unsafe.Add(ref ee0, -1) += Unsafe.Add(ref ee2, -1);
                    ee2 = k00_20 * aa - k01_21 * Unsafe.Add(ref aa, 1);
                    Unsafe.Add(ref ee2, -1) = k01_21 * aa + k00_20 * Unsafe.Add(ref aa, 1);
                    aa = ref Unsafe.Add(ref aa, 8);

                    k00_20 = Unsafe.Add(ref ee0, -2) - Unsafe.Add(ref ee2, -2);
                    k01_21 = Unsafe.Add(ref ee0, -3) - Unsafe.Add(ref ee2, -3);
                    Unsafe.Add(ref ee0, -2) += Unsafe.Add(ref ee2, -2);
                    Unsafe.Add(ref ee0, -3) += Unsafe.Add(ref ee2, -3);
                    Unsafe.Add(ref ee2, -2) = k00_20 * aa - k01_21 * Unsafe.Add(ref aa, 1);
                    Unsafe.Add(ref ee2, -3) = k01_21 * aa + k00_20 * Unsafe.Add(ref aa, 1);
                    aa = ref Unsafe.Add(ref aa, 8);

                    k00_20 = Unsafe.Add(ref ee0, -4) - Unsafe.Add(ref ee2, -4);
                    k01_21 = Unsafe.Add(ref ee0, -5) - Unsafe.Add(ref ee2, -5);
                    Unsafe.Add(ref ee0, -4) += Unsafe.Add(ref ee2, -4);
                    Unsafe.Add(ref ee0, -5) += Unsafe.Add(ref ee2, -5);
                    Unsafe.Add(ref ee2, -4) = k00_20 * aa - k01_21 * Unsafe.Add(ref aa, 1);
                    Unsafe.Add(ref ee2, -5) = k01_21 * aa + k00_20 * Unsafe.Add(ref aa, 1);
                    aa = ref Unsafe.Add(ref aa, 8);

                    k00_20 = Unsafe.Add(ref ee0, -6) - Unsafe.Add(ref ee2, -6);
                    k01_21 = Unsafe.Add(ref ee0, -7) - Unsafe.Add(ref ee2, -7);
                    Unsafe.Add(ref ee0, -6) += Unsafe.Add(ref ee2, -6);
                    Unsafe.Add(ref ee0, -7) += Unsafe.Add(ref ee2, -7);
                    Unsafe.Add(ref ee2, -6) = k00_20 * aa - k01_21 * Unsafe.Add(ref aa, 1);
                    Unsafe.Add(ref ee2, -7) = k01_21 * aa + k00_20 * Unsafe.Add(ref aa, 1);
                    aa = ref Unsafe.Add(ref aa, 8);

                    ee0 = ref Unsafe.Add(ref ee0, -8);
                    ee2 = ref Unsafe.Add(ref ee2, -8);
                }
            }

            void step3_inner_r_loop(int lim, float[] e, int d0, int k_off, int k1)
            {
                float k00_20, k01_21;

                ref float e0 = ref Unsafe.Add(ref e[0], d0);
                ref float e2 = ref Unsafe.Add(ref e0, k_off);
                ref float aa = ref _a[0];

                for (int i = lim >> 2; i > 0; --i)
                {
                    k00_20 = e0 - e2;
                    k01_21 = Unsafe.Add(ref e0, -1) - Unsafe.Add(ref e2, -1);
                    e0 += e2;
                    Unsafe.Add(ref e0, -1) += Unsafe.Add(ref e2, -1);
                    e2 = k00_20 * aa - k01_21 * Unsafe.Add(ref aa, 1);
                    Unsafe.Add(ref e2, -1) = k01_21 * aa + k00_20 * Unsafe.Add(ref aa, 1);

                    aa = ref Unsafe.Add(ref aa, k1);

                    k00_20 = Unsafe.Add(ref e0, -2) - Unsafe.Add(ref e2, -2);
                    k01_21 = Unsafe.Add(ref e0, -3) - Unsafe.Add(ref e2, -3);
                    Unsafe.Add(ref e0, -2) += Unsafe.Add(ref e2, -2);
                    Unsafe.Add(ref e0, -3) += Unsafe.Add(ref e2, -3);
                    Unsafe.Add(ref e2, -2) = k00_20 * aa - k01_21 * Unsafe.Add(ref aa, 1);
                    Unsafe.Add(ref e2, -3) = k01_21 * aa + k00_20 * Unsafe.Add(ref aa, 1);

                    aa = ref Unsafe.Add(ref aa, k1);

                    k00_20 = Unsafe.Add(ref e0, -4) - Unsafe.Add(ref e2, -4);
                    k01_21 = Unsafe.Add(ref e0, -5) - Unsafe.Add(ref e2, -5);
                    Unsafe.Add(ref e0, -4) += Unsafe.Add(ref e2, -4);
                    Unsafe.Add(ref e0, -5) += Unsafe.Add(ref e2, -5);
                    Unsafe.Add(ref e2, -4) = k00_20 * aa - k01_21 * Unsafe.Add(ref aa, 1);
                    Unsafe.Add(ref e2, -5) = k01_21 * aa + k00_20 * Unsafe.Add(ref aa, 1);

                    aa = ref Unsafe.Add(ref aa, k1);

                    k00_20 = Unsafe.Add(ref e0, -6) - Unsafe.Add(ref e2, -6);
                    k01_21 = Unsafe.Add(ref e0, -7) - Unsafe.Add(ref e2, -7);
                    Unsafe.Add(ref e0, -6) += Unsafe.Add(ref e2, -6);
                    Unsafe.Add(ref e0, -7) += Unsafe.Add(ref e2, -7);
                    Unsafe.Add(ref e2, -6) = k00_20 * aa - k01_21 * Unsafe.Add(ref aa, 1);
                    Unsafe.Add(ref e2, -7) = k01_21 * aa + k00_20 * Unsafe.Add(ref aa, 1);

                    aa = ref Unsafe.Add(ref aa, k1);

                    e0 = ref Unsafe.Add(ref e0, -8);
                    e2 = ref Unsafe.Add(ref e2, -8);
                }
            }

            void step3_inner_s_loop(int n, float[] e, int i_off, int k_off, int a, int a_off, int k0)
            {
                var A0 = _a[a];
                var A1 = _a[a + 1];
                var A2 = _a[a + a_off];
                var A3 = _a[a + a_off + 1];
                var A4 = _a[a + a_off * 2];
                var A5 = _a[a + a_off * 2 + 1];
                var A6 = _a[a + a_off * 3];
                var A7 = _a[a + a_off * 3 + 1];

                float k00, k11;

                ref float ee0 = ref Unsafe.Add(ref e[0], i_off);
                ref float ee2 = ref Unsafe.Add(ref ee0, k_off);

                for (int i = n; i > 0; --i)
                {
                    k00 = ee0 - ee2;
                    k11 = Unsafe.Add(ref ee0, -1) - Unsafe.Add(ref ee2, -1);
                    ee0 += ee2;
                    Unsafe.Add(ref ee0, -1) += Unsafe.Add(ref ee2, -1);
                    ee2 = k00 * A0 - k11 * A1;
                    Unsafe.Add(ref ee2, -1) = k11 * A0 + k00 * A1;

                    k00 = Unsafe.Add(ref ee0, -2) - Unsafe.Add(ref ee2, -2);
                    k11 = Unsafe.Add(ref ee0, -3) - Unsafe.Add(ref ee2, -3);
                    Unsafe.Add(ref ee0, -2) += Unsafe.Add(ref ee2, -2);
                    Unsafe.Add(ref ee0, -3) += Unsafe.Add(ref ee2, -3);
                    Unsafe.Add(ref ee2, -2) = k00 * A2 - k11 * A3;
                    Unsafe.Add(ref ee2, -3) = k11 * A2 + k00 * A3;

                    k00 = Unsafe.Add(ref ee0, -4) - Unsafe.Add(ref ee2, -4);
                    k11 = Unsafe.Add(ref ee0, -5) - Unsafe.Add(ref ee2, -5);
                    Unsafe.Add(ref ee0, -4) += Unsafe.Add(ref ee2, -4);
                    Unsafe.Add(ref ee0, -5) += Unsafe.Add(ref ee2, -5);
                    Unsafe.Add(ref ee2, -4) = k00 * A4 - k11 * A5;
                    Unsafe.Add(ref ee2, -5) = k11 * A4 + k00 * A5;

                    k00 = Unsafe.Add(ref ee0, -6) - Unsafe.Add(ref ee2, -6);
                    k11 = Unsafe.Add(ref ee0, -7) - Unsafe.Add(ref ee2, -7);
                    Unsafe.Add(ref ee0, -6) += Unsafe.Add(ref ee2, -6);
                    Unsafe.Add(ref ee0, -7) += Unsafe.Add(ref ee2, -7);
                    Unsafe.Add(ref ee2, -6) = k00 * A6 - k11 * A7;
                    Unsafe.Add(ref ee2, -7) = k11 * A6 + k00 * A7;

                    ee0 = ref Unsafe.Add(ref ee0, -k0);
                    ee2 = ref Unsafe.Add(ref ee2, -k0);
                }
            }

            void step3_inner_s_loop_ld654(int n, float[] e, int i_off, int base_n)
            {
                var a_off = base_n >> 3;
                var A2 = _a[a_off];
                ref float z = ref Unsafe.Add(ref e[0], i_off);
                var count = n;          // 16 floats per iteration

                while (count-- > 0)
                {
                    float k00, k11;

                    k00 = z - Unsafe.Add(ref z, -8);
                    k11 = Unsafe.Add(ref z, -1) - Unsafe.Add(ref z, -9);
                    z += Unsafe.Add(ref z, -8);
                    Unsafe.Add(ref z, -1) += Unsafe.Add(ref z, -9);
                    Unsafe.Add(ref z, -8) = k00;
                    Unsafe.Add(ref z, -9) = k11;

                    k00 = Unsafe.Add(ref z, -2) - Unsafe.Add(ref z, -10);
                    k11 = Unsafe.Add(ref z, -3) - Unsafe.Add(ref z, -11);
                    Unsafe.Add(ref z, -2) += Unsafe.Add(ref z, -10);
                    Unsafe.Add(ref z, -3) += Unsafe.Add(ref z, -11);
                    Unsafe.Add(ref z, -10) = (k00 + k11) * A2;
                    Unsafe.Add(ref z, -11) = (k11 - k00) * A2;

                    k00 = Unsafe.Add(ref z, -12) - Unsafe.Add(ref z, -4);
                    k11 = Unsafe.Add(ref z, -5) - Unsafe.Add(ref z, -13);
                    Unsafe.Add(ref z, -4) += Unsafe.Add(ref z, -12);
                    Unsafe.Add(ref z, -5) += Unsafe.Add(ref z, -13);
                    Unsafe.Add(ref z, -12) = k11;
                    Unsafe.Add(ref z, -13) = k00;

                    k00 = Unsafe.Add(ref z, -14) - Unsafe.Add(ref z, -6);
                    k11 = Unsafe.Add(ref z, -7) - Unsafe.Add(ref z, -15);
                    Unsafe.Add(ref z, -6) += Unsafe.Add(ref z, -14);
                    Unsafe.Add(ref z, -7) += Unsafe.Add(ref z, -15);
                    Unsafe.Add(ref z, -14) = (k00 + k11) * A2;
                    Unsafe.Add(ref z, -15) = (k00 - k11) * A2;

                    iter_54(ref z);
                    iter_54(ref Unsafe.Add(ref z, -8));

                    z = ref Unsafe.Add(ref z, -16);
                }
            }

            private static void iter_54(ref float z)
            {
                float k00, k11, k22, k33;
                float y0, y1, y2, y3;

                k00 = z - Unsafe.Add(ref z, -4);
                y0 = z + Unsafe.Add(ref z, -4);
                y2 = Unsafe.Add(ref z, -2) + Unsafe.Add(ref z, -6);
                k22 = Unsafe.Add(ref z, -2) - Unsafe.Add(ref z, -6);

                z = y0 + y2;
                Unsafe.Add(ref z, -2) = y0 - y2;

                k33 = Unsafe.Add(ref z, -3) - Unsafe.Add(ref z, -7);

                Unsafe.Add(ref z, -4) = k00 + k33;
                Unsafe.Add(ref z, -6) = k00 - k33;

                k11 = Unsafe.Add(ref z, -1) - Unsafe.Add(ref z, -5);
                y1 = Unsafe.Add(ref z, -1) + Unsafe.Add(ref z, -5);
                y3 = Unsafe.Add(ref z, -3) + Unsafe.Add(ref z, -7);

                Unsafe.Add(ref z, -1) = y1 + y3;
                Unsafe.Add(ref z, -3) = y1 - y3;
                Unsafe.Add(ref z, -5) = k11 - k22;
                Unsafe.Add(ref z, -7) = k11 + k22;
            }
        }
    }
}
