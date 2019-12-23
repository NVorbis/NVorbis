using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace NVorbis.Ogg
{
    internal class LightPageReader : IDisposable
    {
        private readonly Dictionary<int, LightPacketProvider> _packetProviders = new Dictionary<int, LightPacketProvider>();
        private readonly HashSet<int> _ignoredSerials = new HashSet<int>();
        private readonly Crc _crc = new Crc();
        private readonly object _readLock = new object();
        private readonly byte[] _headerBuf = new byte[282];
        private readonly byte[] _dataBuf = new byte[65052];

        private Stream _stream;
        private bool _closeOnDispose;
        private readonly Func<LightPacketProvider, bool> _newStreamCallback;
        private long _nextPageOffset;

        public LightPageReader(Stream stream, bool closeOnDispose, Func<LightPacketProvider, bool> newStreamCallback)
        {
            _stream = stream;
            _closeOnDispose = closeOnDispose;
            _newStreamCallback = newStreamCallback;
        }

        internal void Lock()
        {
            Monitor.Enter(_readLock);
        }

        bool CheckLock()
        {
            return Monitor.IsEntered(_readLock);
        }

        internal bool Release()
        {
            if (Monitor.IsEntered(_readLock))
            {
                Monitor.Exit(_readLock);
                return true;
            }
            return false;
        }

        // global values
        public int FoundStreams => _packetProviders.Count;
        public int[] FoundSerials => _packetProviders.Keys.ToArray();
        public int PageCount { get; private set; }
        public long ContainerBits { get; private set; }
        public long WasteBits { get; private set; }

        public long PageOffset { get; private set; }
        public int StreamSerial { get; private set; }
        public int SequenceNumber { get; private set; }
        public PageFlags PageFlags { get; private set; }
        public long GranulePosition { get; private set; }
        public short PacketCount { get; private set; }
        public bool IsResync { get; private set; }

        // look for the next page header, decode it, and check CRC
        internal bool ReadNextPage()
        {
            // make sure we're locked; no sense reading if we aren't
            if (!CheckLock()) throw new InvalidOperationException("Must be locked prior to reading!");

            IsResync = false;
            _stream.Position = _nextPageOffset;
            var ofs = 0;
            int cnt;
            while ((cnt = _stream.Read(_headerBuf, ofs, _headerBuf.Length - ofs)) > 0)
            {
                cnt += ofs;
                for (var i = 0; i < cnt - 4; i++)
                {
                    // look for the capture sequence
                    if (_headerBuf[i] == 0x4f && _headerBuf[i + 1] == 0x67 && _headerBuf[i + 2] == 0x67 && _headerBuf[i + 3] == 0x53)
                    {
                        // cool, found it...

                        // move to the front of the buffer if not there already
                        if (i > 0)
                        {
                            Buffer.BlockCopy(_headerBuf, i, _headerBuf, 0, cnt);
                            WasteBits += i * 8;
                            IsResync = true;

                            // adjust our count and index to match what we just did
                            cnt -= i;
                            i = 0;
                        }

                        // note the file offset
                        var pageOffset = _stream.Position - cnt;

                        // try to make sure we have enough in the buffer
                        cnt += _stream.Read(_headerBuf, cnt, _headerBuf.Length - cnt);

                        // try to load the page
                        if (CheckPage(pageOffset, out var pktCnt, out var nextPageOffset))
                        {
                            // good packet!
                            PacketCount = pktCnt;
                            _nextPageOffset = nextPageOffset;

                            // try to add it to the appropriate packet provider; if it returns false, we're ignoring the page's logical stream
                            if (!AddPage())
                            {
                                // we read a page, but it was for an ignored stream; try looking at the next page position
                                _stream.Position = nextPageOffset;

                                // the simplest way to do this is to jump to the outer loop and force a complete re-start of the process
                                // reset ofs so we're at the beginning of the buffer again
                                ofs = 0;
                                // reset cnt so the move logic at the bottom of the outer loop doesn't run
                                cnt = 0;

                                // update WasteBits since we just threw away an entire page
                                WasteBits += 8 * (nextPageOffset - pageOffset);

                                // bail out to the outer loop
                                break;
                            }
                            return true;
                        }

                        // meh, just reset the stream position to where it was before we tried that page
                        _stream.Position = pageOffset + cnt;
                    }
                }

                // no dice...  try again with a full buffer read
                if (cnt >= 3)
                {
                    _headerBuf[0] = _headerBuf[cnt - 3];
                    _headerBuf[1] = _headerBuf[cnt - 2];
                    _headerBuf[2] = _headerBuf[cnt - 1];
                    ofs = 3;
                    WasteBits += 8 * (cnt - 3);
                    IsResync = true;
                }
            }

            if (cnt == 0)
            {
                // we're EOF
                foreach (var pp in _packetProviders)
                {
                    pp.Value.SetEndOfStream();
                }
            }

            return false;
        }

        private bool CheckPage(long pageOffset, out short packetCount, out long nextPageOffset)
        {
            if (DecodeHeader())
            {
                // we have a potentially good page... check the CRC
                var crc = BitConverter.ToUInt32(_headerBuf, 22);
                var segCount = _headerBuf[26];

                _crc.Reset();
                for (var j = 0; j < 22; j++)
                {
                    _crc.Update(_headerBuf[j]);
                }
                _crc.Update(0);
                _crc.Update(0);
                _crc.Update(0);
                _crc.Update(0);
                _crc.Update(segCount);

                // while we're here, count up the number of packets in the page
                var dataLen = 0;
                short pktCnt = 0;
                for (var j = 0; j < segCount; j++)
                {
                    var segLen = _headerBuf[27 + j];
                    _crc.Update(segLen);
                    dataLen += segLen;
                    if (segLen < 255 || j == segCount - 1)
                    {
                        if (segLen > 0)
                        {
                            ++pktCnt;
                        }
                    }
                }
                packetCount = pktCnt;

                // finish calculating the CRC
                _stream.Position = pageOffset + 27 + segCount;
                if (_stream.Read(_dataBuf, 0, dataLen) < dataLen)
                {
                    // we're going to assume this means the stream has ended
                    nextPageOffset = 0;
                    return false;
                }
                for (var j = 0; j < dataLen; j++)
                {
                    _crc.Update(_dataBuf[j]);
                }

                if (_crc.Test(crc))
                {
                    // cool, we have a valid page!
                    nextPageOffset = _stream.Position;
                    PageOffset = pageOffset;
                    PageCount++;
                    ContainerBits += 8 * (27 + segCount);
                    return true;
                }
            }
            packetCount = 0;
            nextPageOffset = 0;
            return false;
        }

        private bool AddPage()
        {
            if (!_packetProviders.ContainsKey(StreamSerial))
            {
                if (_ignoredSerials.Contains(StreamSerial))
                {
                    // nevermind... we're supposed to ignore these
                    return false;
                }

                var packetProvider = new LightPacketProvider(this);
                _packetProviders.Add(StreamSerial, packetProvider);
                if (!_newStreamCallback(packetProvider))
                {
                    _packetProviders.Remove(StreamSerial);
                    _ignoredSerials.Add(StreamSerial);
                    packetProvider.Dispose();
                    return false;
                }
            }
            else
            {
                _packetProviders[StreamSerial].AddPage();
            }

            return true;
        }

        internal bool ReadPageAt(long offset)
        {
            // make sure we're locked; no sense reading if we aren't
            if (!CheckLock()) throw new InvalidOperationException("Must be locked prior to reading!");

            // this should be safe; we've already checked the page by now

            _stream.Position = offset;
            _stream.Read(_headerBuf, 0, 27);
            _stream.Read(_headerBuf, 27, _headerBuf[26]);

            if (DecodeHeader())
            {
                PageOffset = offset;
                return true;
            }
            return false;
        }

        private bool DecodeHeader()
        {
            if (_headerBuf[0] == 0x4f && _headerBuf[1] == 0x67 && _headerBuf[2] == 0x67 && _headerBuf[3] == 0x53 && _headerBuf[4] == 0)
            {
                PageFlags = (PageFlags)_headerBuf[5];
                GranulePosition = BitConverter.ToInt64(_headerBuf, 6);
                StreamSerial = BitConverter.ToInt32(_headerBuf, 14);
                SequenceNumber = BitConverter.ToInt32(_headerBuf, 18);
                return true;
            }
            return false;
        }

        internal List<Tuple<long, int>> GetPackets(out bool lastContinues)
        {
            var segCnt = _headerBuf[26];
            var dataOffset = PageOffset + 27 + segCnt;
            var packets = new List<Tuple<long, int>>(segCnt);
            lastContinues = false;

            if (segCnt > 0)
            {
                var size = 0;
                for (int i = 0, idx = 27; i < segCnt; i++, idx++)
                {
                    size += _headerBuf[idx];
                    if (_headerBuf[idx] < 255)
                    {
                        if (size > 0)
                        {
                            packets.Add(new Tuple<long, int>(dataOffset, size));
                            dataOffset += size;
                        }
                        size = 0;
                    }
                }
                if (size > 0)
                {
                    packets.Add(new Tuple<long, int>(dataOffset, size));
                    lastContinues = true;
                }
            }
            return packets;
        }

        internal int Read(long offset, byte[] buffer, int index, int count)
        {
            lock (_readLock)
            {
                _stream.Position = offset;
                return _stream.Read(buffer, index, count);
            }
        }

        internal void ReadAllPages()
        {
            if (!CheckLock()) throw new InvalidOperationException("Must be locked!");

            while (ReadNextPage()) { };
        }

        public void Dispose()
        {
            foreach (var pp in _packetProviders)
            {
                pp.Value.Dispose();
            }
            _packetProviders.Clear();

            if (_closeOnDispose)
            {
                _stream?.Dispose();
            }
            _stream = null;
        }
    }
}