using NVorbis.Contracts.Ogg;
using System;
using System.Collections.Generic;

namespace NVorbis.Ogg
{
    class StreamPageReader : IStreamPageReader
    {
        internal static Func<IStreamPageReader, int, Contracts.IPacketProvider> CreatePacketProvider { get; set; } = (pr, ss) => new PacketProvider(pr, ss);

        private readonly IPageData _reader;
        private readonly Contracts.IPacketProvider _packetProvider;
        private readonly List<long> _pageOffsets = new List<long>();

        private int _lastSeqNbr;
        private int? _firstDataPageIndex;

        private int _lastPageIndex = -1;
        private long _lastPageGranulePos;
        private bool _lastPageIsResync;
        private bool _lastPageIsContinuation;
        private bool _lastPageIsContinued;
        private int _lastPagePacketCount;
        private int _lastPageOverhead;

        private ValueTuple<long, int>[] _cachedPagePackets;

        public Contracts.IPacketProvider PacketProvider => _packetProvider;

        public StreamPageReader(IPageData pageReader, int streamSerial)
        {
            _reader = pageReader;
            _packetProvider = CreatePacketProvider(this, streamSerial);
        }

        public void AddPage()
        {
            // verify we haven't read all pages
            if (!HasAllPages)
            {
                // verify the new page's flags
                if ((_reader.PageFlags & PageFlags.EndOfStream) != 0)
                {
                    HasAllPages = true;
                    MaxGranulePosition = _reader.GranulePosition;
                }

                if (_firstDataPageIndex == null && _reader.GranulePosition > 0)
                {
                    _firstDataPageIndex = _pageOffsets.Count;
                }

                if (_reader.IsResync.Value || (_lastSeqNbr != 0 && _lastSeqNbr + 1 != _reader.SequenceNumber))
                {
                    // as a practical matter, if the sequence numbers are "wrong", our logical stream is now out of sync
                    // so whether the page header sync was lost or we just got an out of order page / sequence jump, we're counting it as a resync
                    _pageOffsets.Add(-_reader.PageOffset);
                }
                else
                {
                    _pageOffsets.Add(_reader.PageOffset);
                }

                _lastSeqNbr = _reader.SequenceNumber;
            }
        }

        public ValueTuple<long, int>[] GetPagePackets(int pageIndex)
        {
            if (_cachedPagePackets != null && _lastPageIndex == pageIndex)
            {
                return _cachedPagePackets;
            }

            var pageOffset = _pageOffsets[pageIndex];
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

        public int FindPage(long granulePos)
        {
            // if we're being asked for the first granule, just grab the very first data page
            int pageIndex = -1;
            if (granulePos == 0)
            {
                pageIndex = FindPageForward(_firstDataPageIndex ?? 0, 0, 0);
            }
            else
            {
                // start by looking at the last read page's position...
                var lastPageIndex = _pageOffsets.Count - 1;
                if (GetPageRaw(lastPageIndex, out var pageGP))
                {
                    // most likely, we can look at previous pages for the appropriate one...
                    if (granulePos < pageGP)
                    {
                        pageIndex = FindPageBisection(granulePos, _firstDataPageIndex ?? 0, lastPageIndex);
                    }
                    // unless we're seeking forward, which is merely an excercise in reading forward...
                    else if (granulePos > pageGP)
                    {
                        pageIndex = FindPageForward(lastPageIndex, pageGP, granulePos);
                    }
                    // but of course, it's possible (though highly unlikely) that the last read page ended on the granule we're looking for.
                    else
                    {
                        pageIndex = lastPageIndex;
                    }
                }
            }
            if (pageIndex == -1)
            {
                throw new ArgumentOutOfRangeException(nameof(granulePos));
            }
            return pageIndex;
        }

        private int FindPageForward(int pageIndex, long pageGranulePos, long granulePos)
        {
            while (pageGranulePos < granulePos)
            {
                if (++pageIndex == _pageOffsets.Count)
                {
                    if (!GetNextPageGranulePos(out pageGranulePos))
                    {
                        return -1;
                    }
                }
                else
                {
                    if (!GetPageRaw(pageIndex, out pageGranulePos))
                    {
                        return -1;
                    }
                }
            }
            return pageIndex;
        }

        private bool GetNextPageGranulePos(out long granulePos)
        {
            var pageCount = _pageOffsets.Count;
            while (pageCount == _pageOffsets.Count && !HasAllPages)
            {
                _reader.Lock();
                try
                {
                    if (!_reader.ReadNextPage())
                    {
                        HasAllPages = true;
                        continue;
                    }

                    if (pageCount < _pageOffsets.Count)
                    {
                        granulePos = _reader.GranulePosition;
                        return true;
                    }
                }
                finally
                {
                    _reader.Release();
                }
            }
            granulePos = 0;
            return false;
        }

        private int FindPageBisection(long granulePos, int start, int end)
        {
            var index = (end - 2) / 2 + start;
            while (GetPageRaw(index, out var pageGranulePos))
            {
                if (pageGranulePos > granulePos)
                {
                    end = index;
                    if (start == end - 1)
                    {
                        if (!GetPageRaw(index - 1, out pageGranulePos))
                        {
                            break;
                        }
                        if (pageGranulePos >= granulePos)
                        {
                            return start;
                        }
                        return end;
                    }
                    else if (start == end)
                    {
                        return index;
                    }
                    else
                    {
                        index -= (end - start) / 2;
                    }
                }
                else if (pageGranulePos < granulePos)
                {
                    start = index;
                    if (start >= end - 1 || pageGranulePos == 0)
                    {
                        // if left >= right, we need to read more pages
                        // if left == right - 1, we're just a single page short of the correct one
                        ++index;
                    }
                    else if (start == end)
                    {
                        return index;
                    }
                    else
                    {
                        index += (end - start) / 2;
                    }
                }
                else
                {
                    // direct hit
                    return index;
                }
            }
            return -1;
        }

        private bool GetPageRaw(int pageIndex, out long pageGranulePos)
        {
            var offset = _pageOffsets[pageIndex];
            if (offset < 0)
            {
                offset = -offset;
            }

            _reader.Lock();
            try
            {
                if (_reader.ReadPageAt(offset))
                {
                    pageGranulePos = _reader.GranulePosition;
                    return true;
                }
                pageGranulePos = 0;
                return false;
            }
            finally
            {
                _reader.Release();
            }
        }

        public bool GetPage(int pageIndex, out long granulePos, out bool isResync, out bool isContinuation, out bool isContinued, out int packetCount, out int pageOverhead)
        {
            if (_lastPageIndex == pageIndex)
            {
                granulePos = _lastPageGranulePos;
                isResync = _lastPageIsResync;
                isContinuation = _lastPageIsContinuation;
                isContinued = _lastPageIsContinued;
                packetCount = _lastPagePacketCount;
                pageOverhead = _lastPageOverhead;
                return true;
            }

            // on way or the other, this cached value is invalid at this point
            _cachedPagePackets = null;

            while (pageIndex >= _pageOffsets.Count && !HasAllPages)
            {
                _reader.Lock();
                try
                {
                    if (!_reader.ReadNextPage())
                    {
                        break;
                    }
                }
                finally
                {
                    _reader.Release();
                }
            }

            if (pageIndex < _pageOffsets.Count)
            {
                var offset = _pageOffsets[pageIndex];
                if (offset < 0)
                {
                    isResync = true;
                    offset = -offset;
                }
                else
                {
                    isResync = false;
                }

                _reader.Lock();
                try
                {
                    if (_reader.ReadPageAt(offset))
                    {
                        _lastPageIsResync = isResync;
                        _lastPageGranulePos = granulePos = _reader.GranulePosition;
                        _lastPageIsContinuation = isContinuation = (_reader.PageFlags & PageFlags.ContinuesPacket) != 0;
                        _lastPageIsContinued = isContinued = _reader.IsContinued;
                        _lastPagePacketCount = packetCount = _reader.PacketCount;
                        _lastPageOverhead = pageOverhead = _reader.PageOverhead;
                        _lastPageIndex = pageIndex;
                        return true;
                    }
                }
                finally
                {
                    _reader.Release();
                }
            }

            granulePos = 0;
            isResync = false;
            isContinuation = false;
            isContinued = false;
            packetCount = 0;
            pageOverhead = 0;
            return false;
        }

        public int FillBuffer(long offset, byte[] buffer, int index, int count)
        {
            return _reader.Read(offset, buffer, index, count);
        }

        public void SetEndOfStream()
        {
            HasAllPages = true;
        }

        public int PageCount => _pageOffsets.Count;

        public bool HasAllPages { get; private set; }

        public long? MaxGranulePosition { get; private set; }
    }
}
