/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2012, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * See COPYING for license terms (LGPL).                                    *
 *                                                                          *
 ***************************************************************************/
using System;
using System.Collections.Generic;
using System.IO;

namespace NVorbis
{
    class OggContainerReader : IDisposable
    {
        const uint CRC32_POLY = 0x04c11db7;
        static uint[] crcTable = new uint[256];

        static OggContainerReader()
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
        Dictionary<int, OggPacketReader> _packetReaders;
        Dictionary<int, List<long>> _pageOffsets;
        Dictionary<int, bool> _eosFlags;
        List<int> _streamSerials;
        long _nextPageOffset;

        internal long _containerBits;

        internal Stream BaseStream
        {
            get { return _stream; }
        }

        internal int[] StreamSerials
        {
            get { return _streamSerials.ToArray(); }
        }

        internal OggContainerReader(string fileName)
            : this(File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.Read))
        {

        }

        internal OggContainerReader(Stream stream)
        {
            if (!stream.CanSeek) throw new ArgumentException("stream must be seekable!");

            _stream = stream;

            _packetReaders = new Dictionary<int, OggPacketReader>();
            _pageOffsets = new Dictionary<int, List<long>>();
            _eosFlags = new Dictionary<int, bool>();
            _streamSerials = new List<int>();

            InitContainer();
        }

        public void Dispose()
        {
            _packetReaders.Clear();
            _pageOffsets.Clear();
            _nextPageOffset = 0L;
            _containerBits = 0L;

            _stream.Dispose();
        }

        void InitContainer()
        {
            int streamSerial, seqNo;
            long granulePosition, dataOffset;
            PageFlags pageFlags;
            int[] packetSizes;
            bool lastPacketContinues;

            if (!ReadPageHeader(out streamSerial, out pageFlags, out granulePosition, out seqNo, out dataOffset, out packetSizes, out lastPacketContinues)) throw new InvalidDataException("Not an OGG container!");

            // go ahead and process this first page
            _packetReaders.Add(streamSerial, new OggPacketReader(this, streamSerial));
            _pageOffsets.Add(streamSerial, new List<long>(new long[] { 0L }));
            _eosFlags.Add(streamSerial, false);
            _streamSerials.Add(streamSerial);

            for (int i = 0; i < packetSizes.Length - 1; i++)
            {
                _packetReaders[streamSerial].AddPacket(new OggPacket(_stream, dataOffset, packetSizes[i]) { PageGranulePosition = granulePosition, IsContinued = false, IsFresh = false, IsResync = false, PageSequenceNumber = seqNo });
                dataOffset += packetSizes[i];
            }
            _packetReaders[streamSerial].AddPacket(new OggPacket(_stream, dataOffset, packetSizes[packetSizes.Length - 1]) { PageGranulePosition = granulePosition, IsContinued = lastPacketContinues, IsFresh = false, IsResync = false, PageSequenceNumber = seqNo });
        }

        bool ReadPageHeader(out int streamSerial, out PageFlags flags, out long granulePosition, out int seqNo, out long dataOffset, out int[] packetSizes, out bool lastPacketContinues)
        {
            streamSerial = -1;
            flags = PageFlags.None;
            granulePosition = -1L;
            seqNo = -1;
            dataOffset = -1L;
            packetSizes = null;
            lastPacketContinues = false;

            // header
            var hdrBuf = new byte[27];
            if (_stream.Read(hdrBuf, 0, hdrBuf.Length) != hdrBuf.Length) return false;

            // capture signature
            if (hdrBuf[0] != 0x4f || hdrBuf[1] != 0x67 || hdrBuf[2] != 0x67 || hdrBuf[3] != 0x53) return false;

            // check the stream version
            if (hdrBuf[4] != 0) return false;

            // bit flags
            flags = (PageFlags)hdrBuf[5];

            // granulePosition
            granulePosition = BitConverter.ToInt64(hdrBuf, 6);

            // stream serial
            streamSerial = BitConverter.ToInt32(hdrBuf, 14);

            // sequence number
            seqNo = BitConverter.ToInt32(hdrBuf, 18);

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
            packetSizes = new int[segCnt];
            int size = 0, idx = 0;
            while (--segCnt >= 0)
            {
                var temp = _stream.ReadByte();
                UpdateCRC(temp, ref testCRC);

                packetSizes[idx] += temp;
                if (temp < 255)
                {
                    ++idx;
                    lastPacketContinues = false;
                }
                else
                {
                    lastPacketContinues = true;
                }

                size += temp;
            }
            if (idx < packetSizes.Length)
            {
                var temp = new int[idx];
                for (int i = 0; i < idx; i++)
                {
                    temp[i] = packetSizes[i];
                }
                packetSizes = temp;
            }

            dataOffset = _stream.Position;

            // now we have to go through every byte in the page 
            while (--size >= 0)
            {
                UpdateCRC(_stream.ReadByte(), ref testCRC);
            }

            _nextPageOffset = _stream.Position;
            
            _containerBits += 8 * (27 + segCnt);
            if (testCRC == crc)
            {
                return true;
            }
            _containerBits -= 8 * (27 + segCnt);    // we're going to look for the bits separately...
            return false;
        }

        void UpdateCRC(int nextVal, ref uint crc)
        {
            crc = (crc << 8) ^ crcTable[nextVal ^ (crc >> 24)];
        }

        internal void GatherNextPage(int streamSerial)
        {
            if (_eosFlags[streamSerial]) throw new EndOfStreamException();

            int pageStreamSerial, seqNo;
            long granulePosition, dataOffset;
            PageFlags pageFlags;
            int[] packetSizes;
            bool lastPacketContinues;

            _stream.Position = _nextPageOffset;
            var startPos = _nextPageOffset;

            var isResync = false;
            while (!ReadPageHeader(out pageStreamSerial, out pageFlags, out granulePosition, out seqNo, out dataOffset, out packetSizes, out lastPacketContinues))
            {
                isResync = true;

                // gotta find the next sync header...
                // start on the next byte...
                _containerBits += 8;
                _stream.Position++;

                var cnt = 0;
                while (++cnt < 65536)
                {
                    if (_stream.ReadByte() == 0x4f)
                    {
                        var checkPos = _stream.Position;
                        if (_stream.ReadByte() == 'g' && _stream.ReadByte() == 'g' && _stream.ReadByte() == 'S')
                        {
                            // found it!
                            _stream.Position -= 4;
                            startPos = _stream.Position;
                        }
                        else
                        {
                            _stream.Position = checkPos;
                            _containerBits += 8;
                        }
                    }
                }
                if (cnt == 65536) throw new InvalidDataException("Sync lost and could not find next page.");
            }

            // we now have a parsed header...  generate packets...
            if (!_packetReaders.ContainsKey(streamSerial))
            {
                _packetReaders.Add(streamSerial, new OggPacketReader(this, streamSerial));
                _pageOffsets.Add(streamSerial, new List<long>(new long[] { startPos }));
                _eosFlags.Add(streamSerial, false);
                _streamSerials.Add(streamSerial);
            }

            _packetReaders[streamSerial].AddPacket(new OggPacket(_stream, dataOffset, packetSizes[0]) { PageGranulePosition = granulePosition, IsContinued = false, IsFresh = (pageFlags & PageFlags.FreshPacket) == PageFlags.FreshPacket, IsResync = isResync, PageSequenceNumber = seqNo });
            dataOffset += packetSizes[0];
            for (int i = 1; i < packetSizes.Length - 1; i++)
            {
                _packetReaders[streamSerial].AddPacket(new OggPacket(_stream, dataOffset, packetSizes[i]) { PageGranulePosition = granulePosition, IsContinued = false, IsFresh = false, IsResync = false, PageSequenceNumber = seqNo });
                dataOffset += packetSizes[i];
            }
            if (packetSizes.Length > 1)
            {
                _packetReaders[streamSerial].AddPacket(new OggPacket(_stream, dataOffset, packetSizes[packetSizes.Length - 1]) { PageGranulePosition = granulePosition, IsContinued = lastPacketContinues, IsFresh = false, IsResync = false, IsEndOfStream = (pageFlags & PageFlags.EndOfStream) == PageFlags.EndOfStream, PageSequenceNumber = seqNo });
            }

            if ((pageFlags & PageFlags.EndOfStream) == PageFlags.EndOfStream)
            {
                _eosFlags[streamSerial] = true;
            }
        }

        internal OggPacket GetNextPacket(int streamSerial)
        {
            return _packetReaders[streamSerial].GetNextPacket();
        }

        internal void SetDataStart(int streamSerial)
        {
            _packetReaders[streamSerial].SetDataStart();
        }

        internal long GetLastGranulePos(int streamSerial)
        {
            return _packetReaders[streamSerial].GetLastPacket().PageGranulePosition;
        }

        internal void SeekToSample(int streamSerial, long sampleNum)
        {
            _packetReaders[streamSerial].SeekToGranule(sampleNum);
        }

        internal bool FindNextStream(int currentStreamSerial)
        {
            // goes through all the pages until the serial count increases
            
            // if the index is less than the highest, go ahead and return true
            var idx = Array.IndexOf(StreamSerials, currentStreamSerial);
            var cnt = this._packetReaders.Count;
            if (idx < cnt - 1) return true;

            // read pages until we're done...
            while (cnt == this._packetReaders.Count)
            {
                GatherNextPage(currentStreamSerial);
            }

            return cnt > this._packetReaders.Count;
        }

        internal int GetPageCount(int streamSerial)
        {
            return _packetReaders[streamSerial].GetPageCount();
        }
    }
}
