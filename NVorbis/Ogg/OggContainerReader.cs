/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2012, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * See COPYING for license terms (Ms-PL).                                   *
 *                                                                          *
 ***************************************************************************/
using System;
using System.Collections.Generic;
using System.IO;

namespace NVorbis.Ogg
{
    class ContainerReader : IPacketProvider
    {
        const uint CRC32_POLY = 0x04c11db7;
        static uint[] crcTable = new uint[256];

        static ContainerReader()
        {
            for (uint i = 0; i < 256; i++)
            {
                uint s = i << 24;
                for (int j = 0; j < 8; ++j)
                {
                    s = (s << 1) ^ (s >= (1U << 31) ? CRC32_POLY : 0);
                }
                crcTable[i] = s;
            }
        }

        Stream _stream;
        Dictionary<int, PacketReader> _packetReaders;
        Dictionary<int, bool> _eosFlags;
        List<int> _streamSerials;
        long _nextPageOffset;
        int _pageCount;
        Action<int> _newStreamCallback;

        System.Threading.Mutex _pageLock = new System.Threading.Mutex(false);

        internal long _containerBits;

        internal int[] StreamSerials
        {
            get { return _streamSerials.ToArray(); }
        }

        internal ContainerReader(Stream stream, Action<int> newStreamCallback)
        {
            if (!stream.CanSeek) throw new ArgumentException("stream must be seekable!");

            _stream = new ThreadSafeStream(stream);

            _packetReaders = new Dictionary<int, PacketReader>();
            _eosFlags = new Dictionary<int, bool>();
            _streamSerials = new List<int>();

            _newStreamCallback = newStreamCallback;
        }

        void IPacketProvider.Init()
        {
            GatherNextPage("Not an OGG container!");
        }

        void IDisposable.Dispose()
        {
            _packetReaders.Clear();
            _nextPageOffset = 0L;
            _containerBits = 0L;

            _stream.Dispose();
        }

        class PageHeader
        {
            public int StreamSerial { get; set; }
            public PageFlags Flags { get; set; }
            public long GranulePosition { get; set; }
            public int SequenceNumber { get; set; }
            public long DataOffset { get; set; }
            public int[] PacketSizes { get; set; }
            public bool LastPacketContinues { get; set; }
            public bool IsResync { get; set; }
            public byte[] SavedBuffer { get; set; }
        }

        PageHeader ReadPageHeader(long position)
        {
            // set the stream's position
            _stream.Position = position;

            // header
            var hdrBuf = new byte[27];
            if (_stream.Read(hdrBuf, 0, hdrBuf.Length) != hdrBuf.Length) return null;

            // capture signature
            if (hdrBuf[0] != 0x4f || hdrBuf[1] != 0x67 || hdrBuf[2] != 0x67 || hdrBuf[3] != 0x53) return null;

            // check the stream version
            if (hdrBuf[4] != 0) return null;

            // start populating the header
            var hdr = new PageHeader();

            // bit flags
            hdr.Flags = (PageFlags)hdrBuf[5];

            // granulePosition
            hdr.GranulePosition = BitConverter.ToInt64(hdrBuf, 6);

            // stream serial
            hdr.StreamSerial = BitConverter.ToInt32(hdrBuf, 14);

            // sequence number
            hdr.SequenceNumber = BitConverter.ToInt32(hdrBuf, 18);

            // save off the CRC
            var crc = BitConverter.ToUInt32(hdrBuf, 22);

            // start calculating the CRC value for this page
            var testCRC = 0U;
            for (int i = 0; i < 22; i++)
            {
                UpdateCRC(hdrBuf[i], ref testCRC);
            }
            UpdateCRC(0, ref testCRC);
            UpdateCRC(0, ref testCRC);
            UpdateCRC(0, ref testCRC);
            UpdateCRC(0, ref testCRC);
            UpdateCRC(hdrBuf[26], ref testCRC);

            // figure out the length of the page
            var segCnt = (int)hdrBuf[26];
            var packetSizes = new int[segCnt];
            int size = 0, idx = 0;
            for (int i = 0; i < segCnt; i++)
            {
                var temp = _stream.ReadByte();
                UpdateCRC(temp, ref testCRC);

                packetSizes[idx] += temp;
                if (temp < 255)
                {
                    ++idx;
                    hdr.LastPacketContinues = false;
                }
                else
                {
                    hdr.LastPacketContinues = true;
                }

                size += temp;
            }
            if (hdr.LastPacketContinues) ++idx;
            if (idx < packetSizes.Length)
            {
                var temp = new int[idx];
                for (int i = 0; i < idx; i++)
                {
                    temp[i] = packetSizes[i];
                }
                packetSizes = temp;
            }
            hdr.PacketSizes = packetSizes;

            hdr.DataOffset = position + 27 + segCnt;
            hdr.SavedBuffer = new byte[size];

            // load the page data
            if (_stream.Read(hdr.SavedBuffer, 0, size) != size) return null;

            // now we have to go through every byte in the page
            idx = -1;
            while (++idx < size)
            {
                UpdateCRC(hdr.SavedBuffer[idx], ref testCRC);
            }

            if (testCRC == crc)
            {
                _containerBits += 8 * (27 + segCnt);
                ++_pageCount;
                return hdr;
            }
            return null;
        }

        void UpdateCRC(int nextVal, ref uint crc)
        {
            crc = (crc << 8) ^ crcTable[nextVal ^ (crc >> 24)];
        }

        PageHeader FindNextPageHeader()
        {
            var startPos = _nextPageOffset;

            var isResync = false;
            PageHeader hdr;
            while ((hdr = ReadPageHeader(startPos)) == null)
            {
                isResync = true;
                _containerBits += 8;
                _stream.Position = ++startPos;

                var cnt = 0;
                do
                {
                    if (_stream.ReadByte() == 0x4f)
                    {
                        if (_stream.ReadByte() == 0x67 && _stream.ReadByte() == 0x67 && _stream.ReadByte() == 0x53)
                        {
                            // found it!
                            startPos += cnt;
                            break;
                        }
                        else
                        {
                            _stream.Seek(-3, SeekOrigin.Current);
                        }
                    }
                    _containerBits += 8;
                } while (++cnt < 65536);    // we will only search through 64KB of data to find the next sync marker.  if it can't be found, we have a badly corrupted stream.
                if (cnt == 65536) return null;
            }
            hdr.IsResync = isResync;

            _nextPageOffset = hdr.DataOffset;
            for (int i = 0; i < hdr.PacketSizes.Length; i++)
            {
                _nextPageOffset += hdr.PacketSizes[i];
            }

            return hdr;
        }

        bool AddPage(PageHeader hdr)
        {
            // get our packet reader (create one if we have to)
            PacketReader packetReader;
            if (!_packetReaders.TryGetValue(hdr.StreamSerial, out packetReader))
            {
                packetReader = new PacketReader(this, hdr.StreamSerial);
            }

            // get our flags prepped
            var isContinued = false;
            var isContinuation = (hdr.Flags & PageFlags.ContinuesPacket) == PageFlags.ContinuesPacket;
            var isEOS = (hdr.Flags & PageFlags.EndOfStream) == PageFlags.EndOfStream;
            var isResync = hdr.IsResync;

            // add all the packets, making sure to update flags as needed
            var dataOffset = 0;
            var cnt = hdr.PacketSizes.Length;
            foreach (var size in hdr.PacketSizes)
            {
                var packet = new Packet(_stream, hdr.DataOffset + dataOffset, size)
                    {
                        PageGranulePosition = hdr.GranulePosition,
                        IsEndOfStream = isEOS,
                        PageSequenceNumber = hdr.SequenceNumber,
                        IsContinued = isContinued,
                        IsContinuation = isContinuation,
                        IsResync = isResync,
                    };
                packet.SetBuffer(hdr.SavedBuffer, dataOffset);
                packetReader.AddPacket(packet);

                // update the offset into the stream for each packet
                dataOffset += size;

                // only the first packet in a page can be a continuation or resync
                isContinuation = false;
                isResync = false;

                // only the last packet in a page can be continued
                if (--cnt == 1)
                {
                    isContinued = hdr.LastPacketContinues;
                }
            }

            // if the packet reader list doesn't include the serial in question, add it to all the collections and indicate a new stream to the caller
            if (!_packetReaders.ContainsKey(hdr.StreamSerial))
            {
                var ss = hdr.StreamSerial;
                _packetReaders.Add(ss, packetReader);
                _eosFlags.Add(ss, isEOS);
                _streamSerials.Add(ss);

                return true;
            }
            else
            {
                // otherwise, update the end of stream marker for the stream and indicate an existing stream to the caller
                _eosFlags[hdr.StreamSerial] |= isEOS;
                return false;
            }
        }

        internal class PageReaderLock : IDisposable
        {
            System.Threading.Mutex _lock;

            public PageReaderLock(System.Threading.Mutex pageLock)
            {
                (_lock = pageLock).WaitOne();
            }

            public bool Validate(System.Threading.Mutex pageLock)
            {
                return object.ReferenceEquals(pageLock, _lock);
            }

            public void Dispose()
            {
                _lock.ReleaseMutex();
            }
        }

        internal PageReaderLock TakePageReaderLock()
        {
            return new PageReaderLock(_pageLock);
        }

        int GatherNextPage(string noPageErrorMessage)
        {
            var hdr = FindNextPageHeader();
            if (hdr == null)
            {
                throw new InvalidDataException(noPageErrorMessage);
            }
            if (AddPage(hdr))
            {
                var callback = _newStreamCallback;
                if (callback != null) callback(hdr.StreamSerial);
            }
            return hdr.StreamSerial;
        }

        /// <summary>
        /// Gathers pages until finding a page for the stream indicated
        /// </summary>
        internal void GatherNextPage(int streamSerial, PageReaderLock pageLock)
        {
            // pageLock is just so we know the caller took a lock... we don't actually need it for anything else

            if (pageLock == null) throw new ArgumentNullException("pageLock");
            if (!pageLock.Validate(_pageLock)) throw new ArgumentException("pageLock");
            if (!_eosFlags.ContainsKey(streamSerial)) throw new ArgumentOutOfRangeException("streamSerial");

            do
            {
                if (_eosFlags[streamSerial]) throw new EndOfStreamException();
            } while (GatherNextPage("Could not find next page.") != streamSerial);
        }

        DataPacket IPacketProvider.GetNextPacket(int streamSerial)
        {
            return _packetReaders[streamSerial].GetNextPacket();
        }

        long IPacketProvider.GetLastGranulePos(int streamSerial)
        {
            return _packetReaders[streamSerial].GetLastPacket().PageGranulePosition;
        }

        bool IPacketProvider.FindNextStream(int currentStreamSerial)
        {
            // goes through all the pages until the serial count increases

            // if the index is less than the highest, go ahead and return true
            var idx = Array.IndexOf(StreamSerials, currentStreamSerial);
            var cnt = this._packetReaders.Count;
            if (idx < cnt - 1) return true;

            using (var pageLock = TakePageReaderLock())
            {
                // read pages until we're done...
                while (this._packetReaders.Count == cnt)
                {
                    try
                    {
                        GatherNextPage(string.Empty);
                    }
                    catch (InvalidDataException)
                    {
                        break;
                    }
                }

                return cnt > this._packetReaders.Count;
            }
        }

        internal int GetReadPageCount()
        {
            return _pageCount;
        }

        internal int GetTotalPageCount()
        {
            _eosFlags.Add(-1, false);

            // there cannot possibly be another page less than 28 bytes from the end of the file
            while (_stream.Position < _stream.Length - 28)
            {
                using (var pageLock = TakePageReaderLock())
                {
                    GatherNextPage(-1, pageLock);
                }
            }

            _eosFlags.Remove(-1);

            return _pageCount;
        }

        bool IPacketProvider.CanSeek
        {
            get { return true; }
        }

        int IPacketProvider.GetTotalPageCount(int streamSerial)
        {
            return _packetReaders[streamSerial].GetTotalPageCount();
        }

        long IPacketProvider.ContainerBits
        {
            get { return _containerBits; }
        }

        int IPacketProvider.FindPacket(int streamSerial, long granulePos, Func<DataPacket, DataPacket, DataPacket, int> packetGranuleCountCallback)
        {
            // let the packet reader do the dirty work
            return _packetReaders[streamSerial].FindPacket(granulePos, packetGranuleCountCallback);
        }

        void IPacketProvider.SeekToPacket(int streamSerial, int packetIndex)
        {
            _packetReaders[streamSerial].SeekToPacket(packetIndex);
        }

        DataPacket IPacketProvider.GetPacket(int streamSerial, int packetIndex)
        {
            return _packetReaders[streamSerial].GetPacket(packetIndex);
        }
    }
}
