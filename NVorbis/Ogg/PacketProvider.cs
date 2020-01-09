using NVorbis.Contracts;
using NVorbis.Contracts.Ogg;
using System;
using System.Collections.Generic;

namespace NVorbis.Ogg
{
    internal class PacketProvider : IPacketReader
    {
        List<long> _pageOffsets = new List<long>();

        IPageReader _reader;
        int _lastSeqNbr;
        bool _hasAllPages;

        int _pageIndex;
        int _packetIndex;

        Packet _lastPacket;

        long _cachedPageOffset = -1;
        long _cachedPageGranulePos;
        bool _cachedPageIsResync;
        bool _cachedPageIsContinuation;
        bool _cachedPageIsContinued;
        int _cachedPagePacketCount;
        List<Tuple<long, int>> _cachedPagePackets;

        internal PacketProvider(IPageReader reader)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));

            StreamSerial = _reader.StreamSerial;
        }

        public int StreamSerial { get; }

        public bool CanSeek => true;

        public long ContainerBits => _reader?.ContainerBits ?? 0;

        public int PagesRead => _pageOffsets.Count;

        public Action<IPacket> ParameterChangeCallback { get; set; }

        public void AddPage(PageFlags flags, bool isResync, int seqNbr, long pageOffset)
        {
            // verify we're not already ended (_isEndOfStream)
            if (!_hasAllPages)
            {
                // verify the new page's flags
                if ((flags & PageFlags.EndOfStream) != 0)
                {
                    _hasAllPages = true;
                }

                if (isResync || (_lastSeqNbr != 0 && _lastSeqNbr + 1 != seqNbr))
                {
                    // as a practical matter, if the sequence numbers are "wrong", our logical stream is now out of sync
                    // so whether the page header sync was lost or we just got an out of order page / sequence jump, we're counting it as a resync
                    _pageOffsets.Add(-pageOffset);
                }
                else
                {
                    _pageOffsets.Add(pageOffset);
                }

                _lastSeqNbr = seqNbr;
            }
        }

        public void InvalidatePacketCache(IPacket packet)
        {
            if (_lastPacket == packet)
            {
                _lastPacket = null;
            }
        }

        public long GetGranuleCount()
        {
            if (_reader == null) throw new ObjectDisposedException(nameof(PacketProvider));

            _reader.Lock();
            try
            {
                _reader.ReadAllPages();
                _reader.ReadPageAt(Math.Abs(_pageOffsets[_pageOffsets.Count - 1]));
                return _reader.GranulePosition;
            }
            finally
            {
                _reader.Release();
            }
        }

        public int GetTotalPageCount()
        {
            if (_reader == null) throw new ObjectDisposedException(nameof(PacketProvider));

            _reader.Lock();
            try
            {
                _reader.ReadAllPages();
            }
            finally
            {
                _reader.Release();
            }

            return _pageOffsets.Count;
        }

        public IPacket FindPacket(long granulePos, GetPacketGranuleCount getPacketGranuleCount)
        {
            if (_reader == null) throw new ObjectDisposedException(nameof(PacketProvider));

            // look for the page that contains the granulePos requested, then...
            var pageIdx = -1;
            int? firstGranulePage = null;
            while (true)
            {
                if (!GetPage(++pageIdx, out var pageGranulePos, out _, out var isContinuation, out var isContinued, out var packetCount) && _hasAllPages)
                {
                    // couldn't find it...
                    return null;
                }

                if (pageGranulePos > 0 && !firstGranulePage.HasValue)
                {
                    firstGranulePage = pageIdx;
                }

                if (pageGranulePos >= granulePos)
                {
                    // backtrack from the last packet...
                    var packetIndex = packetCount - 1;
                    if (isContinued)
                    {
                        // continued packets don't count
                        --packetIndex;
                    }

                    if (packetIndex > 0)
                    {
                        IPacket pkt;
                        while (--packetIndex > 0)
                        {
                            pkt = GetPacket(pageIdx, packetIndex, out _);

                            pkt.GranulePosition = pageGranulePos;

                            pageGranulePos -= getPacketGranuleCount(pkt, false);
                            if (pageGranulePos < granulePos)
                            {
                                InvalidatePacketCache(pkt);
                                var tmpPkt = GetPacket(pageIdx, packetIndex, out _);
                                tmpPkt.GranulePosition = pkt.GranulePosition;
                                return tmpPkt;
                            }
                        }

                        // we're down to the "first" packet in the list...
                        if (!isContinuation)
                        {
                            // ... which means it's the target packet if we're not a continuation.
                            pkt = GetPacket(pageIdx, 0, out _);
                            if ((firstGranulePage ?? -1) == pageIdx)
                            {
                                // crap...  it's the very first decodable packet...  we gotta give it a better granule position...
                                pageGranulePos = getPacketGranuleCount(pkt, true);

                                // now that we've used bits in that copy, clean up and get a new copy
                                pkt.Done();
                                pkt = GetPacket(pageIdx, 0, out _);
                            }
                            pkt.GranulePosition = pageGranulePos;
                            return pkt;
                        }

                        // of course, if we're a continuation, that means we have to find which page the packet starts on
                        bool goodPage;
                        while ((goodPage = GetPage(--pageIdx, out _, out _, out isContinuation, out _, out packetCount)) && isContinuation && packetCount == 0)
                        {
                        }
                        if (goodPage)
                        {
                            if (isContinuation)
                            {
                                --packetCount;
                            }
                            pkt = GetPacket(pageIdx, packetCount - 1, out _);
                            pkt.GranulePosition = pageGranulePos;
                            return pkt;
                        }
                    }
                }
            }
        }

        public IPacket GetNextPacket()
        {
            var pkt = PeekNextPacket();
            if (pkt != null)
            {
                ++_packetIndex;
            }
            return pkt;
        }

        public IPacket PeekNextPacket()
        {
            IPacket pkt;
            while ((pkt = GetPacket(_pageIndex, _packetIndex, out var isPastPage)) == null && isPastPage)
            {
                if (++_pageIndex == _pageOffsets.Count && _hasAllPages)
                {
                    --_pageIndex;
                    break;
                }
                _packetIndex = 0;
            }
            return pkt;
        }

        public void SeekToPacket(IPacket packet, int preRoll)
        {
            if (_reader == null) throw new ObjectDisposedException(nameof(PacketProvider));

            // save off the packet index in our IPacket implementation
            if (preRoll < 0) throw new ArgumentOutOfRangeException(nameof(preRoll), "Must be positive or zero!");
            if (!(packet is Packet pkt)) throw new ArgumentException("Must be a packet from LightContainerReader!", nameof(packet));

            var pageIdx = pkt.PageIndex;
            var pktIdx = pkt.Index - preRoll;

            while (pktIdx < 0)
            {
                if (pageIdx == 0)
                {
                    // we can't pre-roll any further, so just assume the caller meant "beginning of the stream"
                    pageIdx = 0;
                    pktIdx = 0;
                    break;
                }

                // back up to the previous page
                if (!GetPage(--pageIdx, out _, out _, out var isContinuation, out _, out var packetCount))
                {
                    throw new InvalidOperationException("Couldn't seek to the selected packet; Not found!");
                }

                if (isContinuation)
                {
                    --packetCount;
                }

                pktIdx += packetCount;
            }

            _pageIndex = pageIdx;
            _packetIndex = pktIdx;
        }

        public IPacket GetPacket(int packetIndex)
        {
            if (_reader == null) throw new ObjectDisposedException(nameof(PacketProvider));

            var pageIdx = -1;
            while (true)
            {
                if (!GetPage(++pageIdx, out _, out _, out var isContinuation, out _, out var packetCount) && _hasAllPages)
                {
                    // couldn't find it...
                    return null;
                }

                // if the page is a continuation, the first packet chunk doesn't count
                if (isContinuation)
                {
                    --packetCount;
                }

                // if the index is within the packet count, we're on the correct page
                if (packetIndex < packetCount)
                {
                    return GetPacket(pageIdx, packetIndex, out _);
                }

                // otherwise, deduce the page from the count and try again on the next page
                packetIndex -= packetCount;
            }
        }

        private IPacket GetPacket(int pageIndex, int packetIndex, out bool isPastPage)
        {
            if (_reader == null) throw new ObjectDisposedException(nameof(PacketProvider));
            if (pageIndex < 0) throw new ArgumentOutOfRangeException(nameof(pageIndex));
            if (packetIndex < 0) throw new ArgumentOutOfRangeException(nameof(packetIndex));

            // set this now, because it'll be the general case
            isPastPage = false;

            // if we're returning the same packet as last call, the caller probably wants the same instance...
            if (_lastPacket != null && _lastPacket.PageIndex == pageIndex && _lastPacket.Index == packetIndex)
            {
                return _lastPacket;
            }

            if (!GetPage(pageIndex, out var granulePos, out var isResync, out var isContinuation, out var isContinued, out var packetCount))
            {
                return null;
            }

            // if the requestd packet is after the last packet, let our caller know
            if (packetIndex >= packetCount)
            {
                isPastPage = true;
                return null;
            }

            // if we're not the last packet in the page, don't bother with continuations
            if (isContinued && packetIndex < packetCount - 1)
            {
                isContinued = false;
            }

            // if this is a continuing page, the first packet chunk isn't a new packet; skip it
            if (isContinuation)
            {
                ++packetIndex;
            }

            // create the packet list and add the item to it
            var pktList = new List<Tuple<long, int>>
            {
                GetPagePackets(pageIndex)[packetIndex]
            };

            bool isLastPacket;
            var finalPage = pageIndex;
            if (isContinued)
            {
                // go read the next page(s) that include this packet
                var contPageIdx = pageIndex;
                var keepReading = isContinued;
                while (keepReading)
                {
                    // if we have all pages, we can't satisfy this request
                    if (_hasAllPages)
                    {
                        return null;
                    }
                    if (!GetPage(++contPageIdx, out granulePos, out isResync, out isContinuation, out keepReading, out packetCount))
                    {
                        // no more pages?  In any case, we can't satify the request
                        return null;
                    }

                    // if the next page isn't a continuation or is a resync, the stream is broken so we'll just return what we could get
                    if (!isContinuation || isResync)
                    {
                        break;
                    }

                    // if the next page is continued, only keep reading if there are more packets in the page
                    if (keepReading && packetCount > 0)
                    {
                        keepReading = false;
                    }

                    // add the packet to the list
                    pktList.Add(GetPagePackets(contPageIdx)[0]);
                }

                // we're now the first packet in the final page, so we'll act like it...
                isLastPacket = packetCount == 1;

                // track the final page read
                finalPage = contPageIdx;
            }
            else
            {
                isLastPacket = packetIndex == packetCount - 1;
            }

            // create the packet instance and populate it with the appropriate initial data
            var packet = new Packet(_reader, this, pageIndex, packetIndex, pktList)
            {
                PageGranulePosition = granulePos,
                IsResync = isResync,
            };

            // if we're the last packet completed in the page, set the .GranulePosition
            if (isLastPacket)
            {
                packet.GranulePosition = packet.PageGranulePosition;

                // if we're the last packet completed in the page, no more pages are available, and _hasAllPages is set, set .IsEndOfStream
                if (_hasAllPages && finalPage == _pageOffsets.Count - 1)
                {
                    packet.IsEndOfStream = true;
                }
            }

            // save off the packet and return
            _lastPacket = packet;
            return packet;
        }

        private List<Tuple<long, int>> GetPagePackets(int pageIndex)
        {
            var pageOffset = _pageOffsets[pageIndex];
            if (_cachedPagePackets != null && _cachedPageOffset == pageOffset)
            {
                return _cachedPagePackets;
            }

            if (pageOffset < 0)
            {
                pageOffset = -pageOffset;
            }

            _reader.Lock();
            try
            {
                _reader.ReadPageAt(pageOffset);
                return _reader.GetPackets();
            }
            finally
            {
                _reader.Release();
            }
        }

        private bool GetPage(int pageIndex, out long granulePos, out bool isResync, out bool isContinuation, out bool isContinued, out int packetCount)
        {
            granulePos = 0;
            isResync = false;
            packetCount = 0;
            isContinued = false;
            isContinuation = false;

            // make sure we've read the page
            var targetPageRead = false;
            while (pageIndex >= _pageOffsets.Count && (targetPageRead = GetNextPage(out granulePos, out isResync, out isContinuation, out isContinued, out packetCount)))
            {
            }
            if (pageIndex >= _pageOffsets.Count)
            {
                return false;
            }
            if (!targetPageRead && !GetPageAt(_pageOffsets[pageIndex], out granulePos, out isResync, out isContinuation, out isContinued, out packetCount))
            {
                return false;
            }
            return true;
        }

        private bool GetPageAt(long offset, out long granulePos, out bool isResync, out bool isContinuation, out bool isContinued, out int packetCount)
        {
            if (_cachedPageOffset == offset)
            {
                granulePos = _cachedPageGranulePos;
                isResync = _cachedPageIsResync;
                isContinuation = _cachedPageIsContinuation;
                isContinued = _cachedPageIsContinued;
                packetCount = _cachedPagePacketCount;
                return true;
            }

            long trueOffset;
            if (offset < 0)
            {
                isResync = true;
                trueOffset = -offset;
            }
            else
            {
                isResync = false;
                trueOffset = offset;
            }

            _reader.Lock();
            try
            {
                if (_reader.ReadPageAt(trueOffset))
                {
                    _cachedPageOffset = offset;
                    _cachedPageIsResync = isResync;
                    _cachedPageGranulePos = granulePos = _reader.GranulePosition;
                    _cachedPagePacketCount = packetCount = _reader.PacketCount;
                    _cachedPagePackets = null;
                    _cachedPageIsContinued = isContinued = _reader.IsContinued;
                    _cachedPageIsContinuation = isContinuation = (_reader.PageFlags & PageFlags.ContinuesPacket) != 0;
                    return true;
                }
            }
            finally
            {
                _reader.Release();
            }

            _cachedPageOffset = -1;

            granulePos = 0;
            packetCount = 0;
            isContinued = false;
            isContinuation = false;
            return false;
        }

        private bool GetNextPage(out long granulePos, out bool isResync, out bool isContinuation, out bool isContinued, out int packetCount)
        {
            var pageCount = _pageOffsets.Count;

            _reader.Lock();
            try
            {
                while (_reader.ReadNextPage())
                {
                    if (pageCount < _pageOffsets.Count)
                    {
                        _cachedPageOffset = _reader.PageOffset;
                        _cachedPageIsResync = isResync = _reader.IsResync.Value;
                        _cachedPageGranulePos = granulePos = _reader.GranulePosition;
                        _cachedPagePacketCount = packetCount = _reader.PacketCount;
                        _cachedPagePackets = null;
                        _cachedPageIsContinued = isContinued = _reader.IsContinued;
                        _cachedPageIsContinuation = isContinuation = (_reader.PageFlags & PageFlags.ContinuesPacket) != 0;
                        return true;
                    }
                }
            }
            finally
            {
                _reader.Release();
            }

            _hasAllPages = true;

            granulePos = 0;
            isResync = false;
            packetCount = 0;
            isContinued = false;
            isContinuation = false;
            return false;
        }

        public void SetEndOfStream()
        {
            _hasAllPages = true;
        }

        public void Dispose()
        {
            _pageOffsets.Clear();
            _cachedPageOffset = -1;
            _cachedPagePackets = null;
            _lastPacket = null;
            _hasAllPages = true;
            _reader = null;
        }
    }
}