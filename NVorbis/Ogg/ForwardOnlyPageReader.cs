using NVorbis.Contracts;
using NVorbis.Contracts.Ogg;
using System;
using System.Collections.Generic;
using System.IO;

namespace NVorbis.Ogg
{
    class ForwardOnlyPageReader : IPageReader
    {
        internal static Func<ICrc> CreateCrc { get; set; } = () => new Crc();
        internal static Func<IPageReader, int, IForwardOnlyPacketProvider> CreatePacketProvider { get; set; } = (pr, ss) => new ForwardOnlyPacketProvider(pr, ss);

        private readonly Dictionary<int, IForwardOnlyPacketProvider> _packetProviders = new Dictionary<int, IForwardOnlyPacketProvider>();
        private readonly HashSet<int> _ignoredSerials = new HashSet<int>();
        private readonly ICrc _crc = CreateCrc();
        private readonly byte[] _headerBuf = new byte[282];

        private Stream _stream;
        private bool _closeOnDispose;
        private Func<IPacketProvider, bool> _newStreamCallback;

        public ForwardOnlyPageReader(Stream stream, bool closeOnDispose, Func<IPacketProvider, bool> newStreamCallback)
        {
            _stream = stream;
            _closeOnDispose = closeOnDispose;
            _newStreamCallback = newStreamCallback;
        }

        public long ContainerBits { get; private set; }

        public long WasteBits { get; private set; }


        // Network streams don't always return the requested size immediately, so this
        // method is used to ensure we fill the buffer if it is possible.
        // Note that it will loop until getting a certain count of zero reads (default: 10).
        // This means in most cases, the network stream probably died by the time we return
        // a short read.
        private int EnsureRead(byte[] buf, int index, int count, int maxTries = 10)
        {
            var read = 0;
            var tries = 0;
            do
            {
                var cnt = _stream.Read(buf, index + read, count - read);
                if (cnt == 0 && ++tries == maxTries)
                {
                    break;
                }
                read += cnt;
            } while (read < count);
            return read;
        }

        public bool ReadNextPage()
        {
            // allocate enough for a full page...
            // 255 * 255 + 26 = 65051
            var isResync = false;

            var ofs = 0;
            int cnt;
            while ((cnt = EnsureRead(_headerBuf, ofs, 26 - ofs)) > 0)
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
                            WasteBits += 8 * i;
                            isResync = true;
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
            count += EnsureRead(buf, count, 27 - count);
            if (count < 27) return false;

            var segCnt = buf[26];
            count += EnsureRead(buf, count, 27 + segCnt - count);
            if (count < 27 + segCnt) return false;

            var dataLen = 0;
            for (var i = 27; i < count; i++)
            {
                dataLen += buf[i];
            }

            var temp = new byte[dataLen + segCnt + 27];
            Buffer.BlockCopy(buf, 0, temp, 0, segCnt + 27);
            buf = temp;

            if (EnsureRead(buf, count, dataLen) != dataLen) return false;
            dataLen += count;

            _crc.Reset();
            for (var i = 0; i < dataLen; i++)
            {
                if (i > 21 && i < 26)
                {
                    _crc.Update(0);
                }
                else
                {
                    _crc.Update(buf[i]);
                }
            }
            var crc = BitConverter.ToUInt32(buf, 22);
            if (_crc.Test(crc))
            {
                var ss = BitConverter.ToInt32(buf, 14);
                if (AddPage(ss, buf, isResync))
                {
                    ContainerBits += 8 * (27 + segCnt);
                    return true;
                }
            }
            return false;
        }

        private bool AddPage(int streamSerial, byte[] buf, bool isResync)
        {
            if (_packetProviders.TryGetValue(streamSerial, out var pp))
            {
                pp.AddPage(buf, isResync);
                if (((PageFlags)buf[5] & PageFlags.EndOfStream) != 0)
                {
                    // if it's done, just remove it
                    _packetProviders.Remove(streamSerial);
                }
            }
            else if (!_ignoredSerials.Contains(streamSerial))
            {
                // don't bother loading the page if the stream says it has no more data
                if (((PageFlags)buf[5] & PageFlags.EndOfStream) != 0)
                {
                    return false;
                }

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

        bool IPageReader.ReadPageAt(long offset) => throw new NotSupportedException();
        void IPageReader.Lock() { }
        bool IPageReader.Release() => false;
    }
}
