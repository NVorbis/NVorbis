using System;
using System.Collections.Generic;
using System.Linq;

namespace NVorbis.Ogg
{
    internal class LightPacketProvider : IPacketProvider
    {
        List<long> _pageOffsets = new List<long>();
        List<long> _pageGranules = new List<long>();
        List<short> _pagePacketCounts = new List<short>();
        List<bool> _pageContinuations = new List<bool>();
        Dictionary<int, int> _packetGranuleCounts = new Dictionary<int, int>();
        Dictionary<int, long> _packetGranulePositions = new Dictionary<int, long>();

        LightPageReader _reader;
        int _lastSeqNbr;
        bool _isEndOfStream;
        int _packetIndex;
        int _packetCount;
        LightPacket _lastPacket;
        List<Tuple<long, int>> _cachedSegments;
        int _cachedPageSeqNo;
        bool _cachedIsResync;
        bool _cachedLastContinues;
        int? _cachedPageIndex;

        internal LightPacketProvider(LightPageReader reader)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));

            StreamSerial = _reader.StreamSerial;

            AddPage();
        }

        public int StreamSerial { get; }

        public bool CanSeek => true;

        public long ContainerBits => _reader?.ContainerBits ?? 0;

        public event EventHandler<ParameterChangeEventArgs> ParameterChange;

        internal void AddPage()
        {
            // The Ogg spec states that partial packets are counted on their _ending_ page.
            // We count them on their _starting_ page.
            // This makes it simpler to read them in GetPacket(int).
            // As a practical matter, our storage is opaque so it really doesn't matter how we count them,
            //  as long as our indexing works out the same as the encoder's.

            // verify we're not already ended (_isEndOfStream)
            if (!_isEndOfStream)
            {
                // verify the new page's flags
                var isCont = false;
                var eos = false;
                if (_reader.PageFlags != PageFlags.None)
                {
                    isCont = (_reader.PageFlags & PageFlags.ContinuesPacket) != 0;
                    if ((_reader.PageFlags & PageFlags.BeginningOfStream) != 0)
                    {
                        isCont = false;

                        // if we're not at the beginning of the stream, something is wrong
                        // BUT, I'm not sure it really matters...  just ignore the issue for now
                    }
                    if ((_reader.PageFlags & PageFlags.EndOfStream) != 0)
                    {
                        eos = true;
                    }
                }

                if (_reader.IsResync || (_lastSeqNbr != 0 && _lastSeqNbr + 1 != _reader.SequenceNumber))
                {
                    // as a practical matter, if the sequence numbers are "wrong", our logical stream is now out of sync
                    // so whether the page header sync was lost or we just got an out of order page / sequence jump, we're counting it as a resync
                    _pageOffsets.Add(-_reader.PageOffset);
                }
                else
                {
                    _pageOffsets.Add(_reader.PageOffset);
                }

                var pktCnt = _reader.PacketCount;
                if (isCont)
                {
                    --pktCnt;
                }

                _pageGranules.Add(_reader.GranulePosition);
                _pagePacketCounts.Add(pktCnt);
                _pageContinuations.Add(isCont && !_reader.IsResync);
                _lastSeqNbr = _reader.SequenceNumber;

                _packetCount += pktCnt;

                _isEndOfStream |= eos;
            }
        }

        internal void SetPacketGranuleInfo(int index, int granuleCount, long granulePos)
        {
            _packetGranuleCounts[index] = granuleCount;
            if (granulePos > 0)
            {
                _packetGranulePositions[index] = granulePos;
            }
        }

        public long GetGranuleCount()
        {
            if (_reader == null) throw new ObjectDisposedException(nameof(LightPacketProvider));

            _reader.Lock();
            _reader.ReadAllPages();
            _reader.Release();

            return _pageGranules[_pageGranules.Count - 1];
        }

        public int GetTotalPageCount()
        {
            if (_reader == null) throw new ObjectDisposedException(nameof(LightPacketProvider));

            _reader.Lock();
            _reader.ReadAllPages();
            _reader.Release();

            return _pageOffsets.Count;
        }

        public DataPacket FindPacket(long granulePos, Func<DataPacket, DataPacket, int> packetGranuleCountCallback)
        {
            if (_reader == null) throw new ObjectDisposedException(nameof(LightPacketProvider));

            // look for the page that contains the granulePos requested, then
            var pageIdx = 0;
            var pktIdx = 0;
            while (pageIdx < _pageGranules.Count && _pageGranules[pageIdx] < granulePos && !_isEndOfStream)
            {
                pktIdx += _pagePacketCounts[pageIdx];
                if (++pageIdx == _pageGranules.Count)
                {
                    if (!GetNextPage())
                    {
                        // couldn't find it
                        return null;
                    }
                }
            }

            // look for the packet that contains the granulePos requested
            var pkt = GetPacket(--pktIdx);
            do
            {
                var prvPkt = pkt;
                pkt = GetPacket(++pktIdx);
                if (pkt == null)
                {
                    return null;
                }
                if (!_packetGranuleCounts.ContainsKey(((LightPacket)pkt).Index))
                {
                    pkt.GranuleCount = packetGranuleCountCallback(pkt, prvPkt);
                }
                pkt.GranulePosition = prvPkt.GranulePosition + pkt.GranuleCount.Value;
                prvPkt.Done();
            }
            while (pkt.GranulePosition <= granulePos);

            // if we get to here, that means we found the correct packet
            return pkt;
        }

        public DataPacket GetNextPacket()
        {
            var pkt = GetPacket(_packetIndex);
            if (pkt != null)
            {
                ++_packetIndex;
            }
            return pkt;
        }

        public DataPacket PeekNextPacket()
        {
            return GetPacket(_packetIndex);
        }

        public void SeekToPacket(DataPacket packet, int preRoll)
        {
            if (_reader == null) throw new ObjectDisposedException(nameof(LightPacketProvider));

            // save off the packet index in our DataPacket implementation
            if (preRoll < 0) throw new ArgumentOutOfRangeException(nameof(preRoll), "Must be positive or zero!");
            if (!(packet is LightPacket pkt)) throw new ArgumentException("Must be a packet from LightContainerReader!", nameof(packet));

            // we can seek back to the first packet, but no further
            _packetIndex = Math.Max(0, pkt.Index - preRoll);
        }

        public DataPacket GetPacket(int packetIndex)
        {
            if (_reader == null) throw new ObjectDisposedException(nameof(LightPacketProvider));

            // if we're returning the same packet as last call, the caller probably wants the same instance...
            if (_lastPacket != null && _lastPacket.Index == packetIndex)
            {
                return _lastPacket;
            }

            // figure out which page the requested packet starts on, and which packet it is in the sequence
            var pageIndex = 0;
            var pktIdx = packetIndex;
            while (pageIndex < _pagePacketCounts.Count && pktIdx >= _pagePacketCounts[pageIndex])
            {
                pktIdx -= _pagePacketCounts[pageIndex];
                if (++pageIndex == _pageContinuations.Count && !_isEndOfStream)
                {
                    if (!GetNextPage())
                    {
                        // no more pages
                        _isEndOfStream = true;
                        return null;
                    }
                }
            }
            if (pageIndex == _pagePacketCounts.Count)
            {
                // couldn't find it
                return null;
            }
            // if the found page is a continuation, ignore the first packet (it's the continuation)
            if (_pageContinuations[pageIndex])
            {
                ++pktIdx;
            }

            var pktList = new List<Tuple<long, int>>();

            // get all the packets in the page (including continued / continuations)
            var pkts = GetPagePackets(pageIndex, out var lastContinues, out var isResync, out var pageSeqNo);
            pktList.Add(pkts[pktIdx]);

            // if our packet is continued, read in the rest of it from the next page(s)
            var startPageIdx = pageIndex;
            var keepReading = lastContinues;
            while (keepReading && pktIdx >= _pagePacketCounts[pageIndex] - 1)
            {
                if (++pageIndex == _pagePacketCounts.Count)
                {
                    if (_isEndOfStream)
                    {
                        // per the spec, a continued packet at the end of the stream should be dropped
                        return null;
                    }
                    if (!GetNextPage())
                    {
                        // no more pages
                        _isEndOfStream = true;
                        return null;
                    }
                }

                pktIdx = 0;
                pkts = GetPagePackets(pageIndex, out keepReading, out var contResync, out pageSeqNo);
                if (contResync)
                {
                    // if we're in a resync, just return what we could get.
                    break;
                }
                pktList.Add(pkts[0]);
            }

            // create the packet instance and populate it with the appropriate initial data
            var packet = new LightPacket(_reader, this, packetIndex, pktList) {
                PageGranulePosition = _pageGranules[startPageIdx],
                PageSequenceNumber = pageSeqNo,
                IsResync = isResync
            };

            // if we're the last packet completed in the page, set the .GranulePosition
            if (pktIdx == pkts.Count - 1 || pktIdx == 0 && lastContinues)
            {
                packet.GranulePosition = packet.PageGranulePosition;

                // if we're the last packet completed in the page, no more pages are available, and _isEndOfStream is set, set .IsEndOfStream
                if (pageIndex == _pageOffsets.Count - 1 && _isEndOfStream)
                {
                    packet.IsEndOfStream = true;
                }
            }
            if (_packetGranuleCounts.TryGetValue(packetIndex, out var granuleCount))
            {
                packet.GranuleCount = granuleCount;

                if (_packetGranulePositions.TryGetValue(packetIndex, out var granulePos))
                {
                    packet.GranulePosition = granulePos;
                }
            }

            _lastPacket = packet;
            return packet;
        }

        private bool GetNextPage()
        {
            _reader.Lock();
            try
            {
                while (_reader.ReadNextPage() && _packetIndex == _packetCount)
                {
                    // no-op
                }
            }
            finally
            {
                _reader.Release();
            }
            return _packetIndex < _packetCount;
        }

        private List<Tuple<long, int>> GetPagePackets(int pageIndex, out bool lastContinues, out bool isResync, out int pageSeqNo)
        {
            if (_cachedPageIndex.HasValue && _cachedPageIndex.Value == pageIndex)
            {
                pageSeqNo = _cachedPageSeqNo;
                isResync = _cachedIsResync;
                lastContinues = _cachedLastContinues;
                return _cachedSegments;
            }

            var pageOffset = _pageOffsets[pageIndex];

            isResync = pageOffset < 0;
            if (isResync)
            {
                pageOffset *= -1;
            }

            List<Tuple<long, int>> pkts = null;
            lastContinues = false;
            var seqNo = -1;

            _reader.Lock();
            try
            {
                if (_reader.ReadPageAt(pageOffset))
                {
                    seqNo = _reader.SequenceNumber;
                    pkts = _reader.GetPackets(out lastContinues);
                }
            }
            finally
            {
                _reader.Release();
            }

            if (pkts != null)
            {
                if (isResync && _pageContinuations[pageIndex])
                {
                    pkts.RemoveAt(0);
                }
                _cachedPageSeqNo = pageSeqNo = seqNo;
                _cachedIsResync = isResync;
                _cachedPageIndex = pageIndex;
                _cachedLastContinues = lastContinues;
                return _cachedSegments = pkts;
            }

            pageSeqNo = -1;
            isResync = false;
            return null;
        }

        internal void SetEndOfStream()
        {
            _isEndOfStream = true;
        }

        public void Dispose()
        {
            _pageOffsets.Clear();
            _pageGranules.Clear();
            _pagePacketCounts.Clear();
            _pageContinuations.Clear();
            _packetGranuleCounts.Clear();
            _cachedPageIndex = null;
            _cachedSegments = null;
            _lastPacket = null;
            _isEndOfStream = true;
            _packetCount = 0;
            _reader = null;
        }
    }
}