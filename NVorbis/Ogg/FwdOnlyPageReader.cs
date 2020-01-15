using NVorbis.Contracts;
using NVorbis.Contracts.Ogg;
using System;
using System.Collections.Generic;
using System.IO;

namespace NVorbis.Ogg
{
    class FwdOnlyPageReader : IPageReader
    {
        internal static Func<ICrc> CreateCrc { get; set; } = () => new Crc();
        internal static Func<IPageReader, int, IFwdOnlyPacketProvider> CreatePacketProvider { get; set; } = (pr, ss) => new FwdOnlyPacketProvider(pr, ss);

        private readonly Dictionary<int, IFwdOnlyPacketProvider> _packetProviders = new Dictionary<int, IFwdOnlyPacketProvider>();
        private readonly HashSet<int> _ignoredSerials = new HashSet<int>();
        private readonly ICrc _crc = CreateCrc();
        private readonly byte[] _headerBuf = new byte[282];

        private Stream _stream;
        private bool _closeOnDispose;
        private Func<IPacketProvider, bool> _newStreamCallback;

        public FwdOnlyPageReader(Stream stream, bool closeOnDispose, Func<IPacketProvider, bool> newStreamCallback)
        {
            _stream = stream;
            _closeOnDispose = closeOnDispose;
            _newStreamCallback = newStreamCallback;
        }

        public long ContainerBits { get; private set; }

        public long WasteBits { get; private set; }


        public bool ReadNextPage()
        {
            // allocate enough for a full page...
            // 255 * 255 + 26 = 65051
            var isResync = false;

            var ofs = 0;
            int cnt;
            while ((cnt = _stream.Read(_headerBuf, ofs, 26 - ofs)) > 0)
            {
                cnt += ofs;
                for (var i = 0; i < cnt - 4; i++)
                {
                    // look for the capture sequence
                    if (_headerBuf[i] == 0x4f && _headerBuf[i + 1] == 0x67 && _headerBuf[i + 2] == 0x67 && _headerBuf[i + 3] == 0x53)
                    {
                        if (i > 0)
                        {
                            cnt -= i;
                            Buffer.BlockCopy(_headerBuf, i, _headerBuf, 0, cnt);
                            i = 0;
                        }

                        if (ParseHeader(_headerBuf, ref cnt, isResync))
                        {
                            return true;
                        }
                    }

                    WasteBits += 8;
                    isResync = true;
                }

                if (cnt >= 3)
                {
                    _headerBuf[0] = _headerBuf[cnt - 3];
                    _headerBuf[1] = _headerBuf[cnt - 2];
                    _headerBuf[2] = _headerBuf[cnt - 1];
                    ofs = 3;
                }
            }

            if (cnt == 0)
            {
                foreach(var pp in _packetProviders)
                {
                    pp.Value.SetEndOfStream();
                }
            }

            return false;
        }

        private bool ParseHeader(byte[] buf, ref int count, bool isResync)
        {
            count += _stream.Read(buf, count, 27 - count);
            if (count < 27) return false;

            var segCnt = buf[26];
            count += _stream.Read(buf, count, 27 + segCnt - count);
            if (count < 27 + segCnt) return false;

            var dataLen = 0;
            for (var i = 27; i < count; i++)
            {
                dataLen += buf[i];
            }

            var temp = new byte[dataLen + segCnt + 27];
            Buffer.BlockCopy(buf, 0, temp, 0, segCnt + 27);
            buf = temp;

            count += _stream.Read(buf, count, 27 + segCnt + dataLen - count);
            if (count < 27 + segCnt + dataLen) return false;

            _crc.Reset();
            for (var i = 0; i < 22; i++)
            {
                _crc.Update(buf[i]);
            }
            for (var i = 0; i < 4; i++)
            {
                _crc.Update(0);
            }
            for (var i = 26; i < count; i++)
            {
                _crc.Update(buf[i]);
            }
            var crc = BitConverter.ToUInt32(buf, 22);
            if (_crc.Test(crc))
            {
                var ss = BitConverter.ToInt32(buf, 14);
                if (AddPage(ss, buf, count, isResync))
                {
                    ContainerBits += 8 * (27 + segCnt);
                    return true;
                }
            }
            return false;
        }

        private bool AddPage(int streamSerial, byte[] buf, int count, bool isResync)
        {
            if (_packetProviders.TryGetValue(streamSerial, out var pp))
            {
                pp.AddPage(buf, isResync);
            }
            else if (!_ignoredSerials.Contains(streamSerial))
            {
                pp = CreatePacketProvider(this, streamSerial);
                pp.AddPage(buf, isResync);
                _packetProviders.Add(streamSerial, pp);
                if (!_newStreamCallback(pp))
                {
                    _packetProviders.Remove(streamSerial);
                    _ignoredSerials.Add(streamSerial);
                    return false;
                }
            }
            else
            {
                return false;
            }
            return true;
        }

        public void Dispose()
        {
            foreach (var pp in _packetProviders)
            {
                pp.Value.SetEndOfStream();
            }
            _packetProviders.Clear();

            if (_closeOnDispose)
            {
                _stream?.Dispose();
            }
            _stream = null;
        }

        void IPageReader.ReadAllPages() => throw new NotSupportedException();
        bool IPageReader.ReadPageAt(long offset) => throw new NotSupportedException();
        void IPageReader.Lock() { }
        bool IPageReader.Release() => false;
    }
}
