using NVorbis.Contracts;
using NVorbis.Contracts.Ogg;
using System;
using System.Collections.Generic;

namespace NVorbis.Ogg
{
    internal class PacketProvider : IPacketReader
    {
        private readonly List<long> _pageOffsets = new List<long>();

        private IPageReader _reader;
        private int _lastSeqNbr;
        private bool _hasAllPages;

        private int _pageIndex;
        private int _packetIndex;

        private Packet _lastPacket;

        private long _cachedPageOffset = -1;
        private long _cachedPageGranulePos;
        private bool _cachedPageIsResync;
        private bool _cachedPageIsContinuation;
        private bool _cachedPageIsContinued;
        private int _cachedPagePacketCount;
        private List<Tuple<long, int>> _cachedPagePackets;
        private int _cachedPageOverhead;

        internal PacketProvider(IPageReader reader)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));

            StreamSerial = _reader.StreamSerial;
        }

        public int StreamSerial { get; }

        public bool CanSeek => true;

        public long ContainerBits => _reader?.ContainerBits ?? 0;

        public int PagesRead => _pageOffsets.Count;

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
            // explicitly do a referential equality check so two packets of the same page & index don't clobber each other's cache
            if (ReferenceEquals(_lastPacket, packet))
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

        public long SeekTo(long granulePos, int preRoll, GetPacketGranuleCount getPacketGranuleCount)
        {
            if (_reader == null) throw new ObjectDisposedException(nameof(PacketProvider));

            // special case: the first data packet is always the first packet completed on the first page having a non-zero granulePos
            if (granulePos == 0)
            {
                SeekToFirstDataPacket();
                return 0;
            }

            // we want to find the granule _after_ the one being requested; that puts us in the correct position to load the appropriate packet
            ++granulePos;

            // search for the correct page in the stream
            var pageIdx = BisectionFindPage(granulePos, out var pageGranulePos, out var isContinuation, out var packetCount);

            // now we have to figure out the correct packet...
            var pktIdx = packetCount - 1;
            while (pageIdx > 0 || pktIdx > 0)
            {
                // if we're before the first full packet in the page, move to the previous page
                if (pktIdx == -1 || (isContinuation && pktIdx == 0))
                {
                    var wasContinuation = isContinuation;
                    if (!GetPage(--pageIdx, out pageGranulePos, out _, out isContinuation, out var isContinued, out packetCount, out _))
                    {
                        throw new System.IO.InvalidDataException("Could not load page!");
                    }
                    if (!isContinued && wasContinuation)
                    {
                        throw new System.IO.InvalidDataException("Expected contined page, found non-continued page.");
                    }
                    pktIdx = packetCount - 1;
                }

                // if we haven't gotten to the packet containing the position, move back by the size of the current packet
                if (pageGranulePos >= granulePos)
                {
                    // subtract out the packet's granuleCount
                    var packet = GetPacket(pageIdx, pktIdx, out _);
                    if (packet == null)
                    {
                        throw new System.IO.InvalidDataException("Could not load packet!");
                    }
                    pageGranulePos -= getPacketGranuleCount(packet, false);
                    packet.Done();

                    // if our granule position is negative, our packet is the first actual data packet
                    if (pageGranulePos < 0)
                    {
                        preRoll = 0;
                    }
                }

                // if we're now before the position, apply the pre-roll OR set the seek position and return the new granule position
                if (pageGranulePos < granulePos)
                {
                    if (preRoll > 0)
                    {
                        --preRoll;
                    }
                    else
                    {
                        // we've found our target packet
                        _pageIndex = pageIdx;
                        _packetIndex = pktIdx;
                        return pageGranulePos;
                    }
                }

                // decrement the packet index
                --pktIdx;
            }
            throw new InvalidOperationException("Could not find the packet for the granule position requested.");
        }

        private int BisectionFindPage(long tgtGranulePos, out long pageGranulePos, out bool isContinuation, out int packetCount)
        {
            // first, find the correct page (bisection search)
            // if we have all pages, use a proper search
            var left = 0;
            var right = _pageOffsets.Count - 1;
            int pageIdx;
            if (_hasAllPages)
            {
                pageIdx = _pageOffsets.Count / 2;
            }
            // otherwise, start at the last page we do have
            else
            {
                pageIdx = right;
            }

            do
            {
                if (GetPage(pageIdx, out pageGranulePos, out _, out isContinuation, out _, out packetCount, out _))
                {
                    if (pageGranulePos >= tgtGranulePos)
                    {
                        right = pageIdx;
                        if (left == right - 1)
                        {
                            // we've found the correct page
                            return pageIdx;
                        }
                        else
                        {
                            pageIdx -= (right - left) / 2;
                        }
                    }
                    else if (pageGranulePos < tgtGranulePos)
                    {
                        left = pageIdx;
                        if (left >= right - 1)
                        {
                            // if left >= right, we need to read more pages
                            // if left == right - 1, we're just a single page short of the correct one
                            ++pageIdx;
                        }
                        else
                        {
                            pageIdx += (right - left) / 2;
                        }
                    }
                }
                else
                {
                    // we ran out of data
                    throw new ArgumentOutOfRangeException(nameof(tgtGranulePos));
                }
            }
            while (true);
        }

        private void SeekToFirstDataPacket()
        {
            var pageIdx = -1;
            bool goodPage;
            bool isContinuation;
            while ((goodPage = GetPage(++pageIdx, out var pageGranulePos, out _, out isContinuation, out _, out _, out _)) && pageGranulePos == 0) { }

            if (!goodPage)
            {
                throw new System.IO.InvalidDataException("Could not find a page with non-zero granule position!");
            }

            if (isContinuation)
            {
                var packetCount = 0;
                while (--pageIdx >= 0 && GetPage(pageIdx, out _, out _, out isContinuation, out var isContinued, out packetCount, out _) && isContinuation && isContinued && packetCount == 0) { }
                _packetIndex = packetCount;
            }
            else
            {
                _packetIndex = 0;
            }
            _pageIndex = pageIdx;
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

            if (!GetPage(pageIndex, out var granulePos, out var isResync, out var isContinuation, out var isContinued, out var packetCount, out var pageOverhead))
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
                    if (!GetPage(++contPageIdx, out granulePos, out isResync, out isContinuation, out keepReading, out packetCount, out var contPageOverhead))
                    {
                        // no more pages?  In any case, we can't satify the request
                        return null;
                    }
                    pageOverhead += contPageOverhead;

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

            // if it's the first packet, associate the container overhead with it
            if (packetIndex == 0 || (isContinuation && packetIndex == 1))
            {
                packet.ContainerOverheadBits = pageOverhead * 8;
            }

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

        private bool GetPage(int pageIndex, out long granulePos, out bool isResync, out bool isContinuation, out bool isContinued, out int packetCount, out int pageOverhead)
        {
            granulePos = 0;
            isResync = false;
            packetCount = 0;
            isContinued = false;
            isContinuation = false;
            pageOverhead = 0;

            // make sure we've read the page
            var targetPageRead = false;
            while (pageIndex >= _pageOffsets.Count && (targetPageRead = GetNextPage(out granulePos, out isResync, out isContinuation, out isContinued, out packetCount, out pageOverhead)))
            {
            }
            if (pageIndex >= _pageOffsets.Count)
            {
                return false;
            }
            if (!targetPageRead && !GetPageAt(_pageOffsets[pageIndex], out granulePos, out isResync, out isContinuation, out isContinued, out packetCount, out pageOverhead))
            {
                return false;
            }
            return true;
        }

        private bool GetPageAt(long offset, out long granulePos, out bool isResync, out bool isContinuation, out bool isContinued, out int packetCount, out int pageOverhead)
        {
            if (_cachedPageOffset == offset)
            {
                granulePos = _cachedPageGranulePos;
                isResync = _cachedPageIsResync;
                isContinuation = _cachedPageIsContinuation;
                isContinued = _cachedPageIsContinued;
                packetCount = _cachedPagePacketCount;
                pageOverhead = _cachedPageOverhead;
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
                    _cachedPageOverhead = pageOverhead = _reader.PageOverhead;
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
            pageOverhead = 0;
            return false;
        }

        private bool GetNextPage(out long granulePos, out bool isResync, out bool isContinuation, out bool isContinued, out int packetCount, out int pageOverhead)
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
                        _cachedPageOverhead = pageOverhead = _reader.PageOverhead;
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
            pageOverhead = 0;
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