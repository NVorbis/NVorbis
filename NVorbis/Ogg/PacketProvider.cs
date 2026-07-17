using NVorbis.Contracts;
using NVorbis.Contracts.Ogg;
using System;
using System.Collections.Generic;

namespace NVorbis.Ogg
{
    class PacketProvider : Contracts.IPacketProvider, IPacketReader
    {
        private readonly IStreamPageReader _reader;

        private int _pageIndex;
        private int _packetIndex;

        private int _lastPacketPageIndex;
        private int _lastPacketPacketIndex;
        private Packet _lastPacket;
        private int _nextPacketPageIndex;
        private int _nextPacketPacketIndex;
        private int _firstAudioPacketIndex;

        public bool CanSeek => true;

        public int StreamSerial { get; }

        public Func<GranuleDiscrepancy, GranuleDiscrepancyResolution?> GranuleDiscrepancyHandler { get; set; }

        internal PacketProvider(IStreamPageReader reader, int streamSerial)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));

            StreamSerial = streamSerial;
        }

        public long GetGranuleCount()
        {
            if (_reader == null) throw new ObjectDisposedException(nameof(PacketProvider));

            if (!_reader.HasAllPages)
            {
                // this will force the reader to attempt to read all pages
                _reader.GetPage(int.MaxValue, out _, out _, out _, out _, out _, out _);
            }
            return _reader.MaxGranulePosition.Value;
        }

        public IPacket GetNextPacket()
        {
            return GetNextPacket(ref _pageIndex, ref _packetIndex);
        }

        public IPacket PeekNextPacket()
        {
            var pageIndex = _pageIndex;
            var packetIndex = _packetIndex;
            return GetNextPacket(ref pageIndex, ref packetIndex);
        }

        public long SeekTo(long granulePos, int preRoll, GetPacketGranuleCount getPacketGranuleCount)
        {
            if (_reader == null) throw new ObjectDisposedException(nameof(PacketProvider));

            int pageIndex = _reader.FindPage(granulePos);
            int packetIndex;

            if (pageIndex <= _reader.FirstDataPageIndex)
            {
                // We are on the very first audio page (or somehow before it). There is no
                // preceding audio page to validate granule positions against, so skip the
                // complex FindPacket logic and snap directly to the stream beginning.
                // If a packet from the previous logical grouping spilled its tail onto this
                // page (the page's continuation flag is set -- issue #37), packet 0 completes
                // that spill and the first fresh packet is index 1. Reading the continuation
                // flag is generic Ogg page positioning, not codec knowledge. We return the raw
                // packet-start granule of that first fresh packet; the codec decides whether/how
                // to normalize it (a stream cut or captured mid-broadcast doesn't start at
                // granule zero -- issue #35).
                pageIndex = _reader.FirstDataPageIndex;
                _reader.GetPage(pageIndex, out _, out _, out var isContinuation, out _, out _, out _);
                _firstAudioPacketIndex = isContinuation ? 1 : 0;
                var (gps, endGP) = GetTargetPageInfo(pageIndex, _firstAudioPacketIndex, 0, getPacketGranuleCount);
                granulePos = gps.Length > _firstAudioPacketIndex ? gps[_firstAudioPacketIndex] : endGP;
                packetIndex = _firstAudioPacketIndex;
            }
            else
            {
                packetIndex = FindPacket(pageIndex, preRoll, ref granulePos, getPacketGranuleCount);

                if (!NormalizePacketIndex(ref pageIndex, ref packetIndex))
                {
                    throw new ArgumentOutOfRangeException(nameof(granulePos));
                }
            }

            _lastPacket = null;
            _pageIndex = pageIndex;
            _packetIndex = packetIndex;
            return granulePos;
        }

        private (long lastPageGranulePos, int lastPagePacketLength, int firstRealPacket) GetPreviousPageInfo(int pageIndex, GetPacketGranuleCount getPacketGranuleCount)
        {
            if (pageIndex > 0)
            {
                int lastPagePacketLength;
                if (_reader.GetPage(pageIndex - 1, out var lastPageGranulePos, out _, out _, out var isContinued, out var lastPacketCount, out _))
                {
                    if (pageIndex > _reader.FirstDataPageIndex)
                    {
                        --pageIndex;
                        var lastPacketIndex = lastPacketCount - 1;
                        // this will either be a continued packet OR the last packet of the last page
                        // in both cases that's precisely the value we need
                        var lastPacket = CreatePacket(ref pageIndex, ref lastPacketIndex, false, 0, false, isContinued, lastPacketCount, 0);
                        if (lastPacket == null)
                        {
                            throw new System.IO.InvalidDataException("Could not find end of continuation!");
                        }
                        lastPagePacketLength = getPacketGranuleCount(lastPacket);
                    }
                    else
                    {
                        lastPagePacketLength = 0;
                    }
                    return (lastPageGranulePos, lastPagePacketLength, isContinued ? 1 : 0);
                }
                throw new System.IO.InvalidDataException("Could not get preceding page?!");
            }
            else
            {
                return (0, 0, 0);
            }
        }

        private (long[] gps, long endGP) GetTargetPageInfo(int pageIndex, int firstRealPacket, int lastPagePacketLength, GetPacketGranuleCount getPacketGranuleCount)
        {
            if (!_reader.GetPage(pageIndex, out var pageGranulePos, out var isResync, out var isContinuation, out var isContinued, out var packetCount, out _))
            {
                throw new System.IO.InvalidDataException("Could not get found page?!");
            }

            if (isContinued)
            {
                // if continued, the last packet index doesn't apply
                packetCount--;
            }

            // get the granule positions of all packets in the page
            var gps = new long[packetCount];
            var endGP = pageGranulePos;
            for (var i = packetCount - 1; i >= firstRealPacket; i--)
            {
                gps[i] = endGP;

                // it would be nice to pass false instead of isContinued, but (hypothetically) we don't know if getPacketGranuleCount(...) needs the whole thing...
                // Vorbis doesn't, but someone might decide to try to use us for another purpose so we'll be good here.
                var packet = CreatePacket(ref pageIndex, ref i, false, pageGranulePos, i == 0 && isResync, isContinued, packetCount, 0);
                if (packet == null)
                {
                    throw new System.IO.InvalidDataException("Could not find end of continuation!");
                }
                endGP -= getPacketGranuleCount(packet);
            }

            // if we're continued, the continued packet ends on our calculated endGP
            if (firstRealPacket == 1)
            {
                gps[0] = endGP;
                endGP -= lastPagePacketLength;
            }

            return (gps, endGP);
        }

        // When the calculated end-granule != the next page's stored granule, hand a positive
        // discrepancy to the codec (it may account for it, e.g. an encoder mis-count) and handle a
        // negative one as a generic EOS-clip over-count. A positive discrepancy the codec declines
        // is a genuine spliced-timeline hole (issue #39).
        private int FindPacket(long[] gps, long endGP, long lastPageGranulePos, int lastPagePacketLength, ref long granulePos)
        {
            if (endGP != lastPageGranulePos)
            {
                var diff = endGP - lastPageGranulePos;
                if (diff > 0)
                {
                    var resolution = GranuleDiscrepancyHandler?.Invoke(
                        new GranuleDiscrepancy(granulePos, endGP, lastPagePacketLength, diff));
                    if (resolution.HasValue)
                    {
                        // the codec accounts for the discrepancy; seek to the packet/granule it returned
                        granulePos = resolution.Value.GranulePos;
                        return resolution.Value.PacketOffset;
                    }
                    // otherwise there's a granule hole between the previous page and this one:
                    // the stream's timeline skips forward, e.g. because it was spliced together
                    // from segments (issue #39).  This page's granule positions are internally
                    // consistent, so gps is already correct; a request that falls inside the
                    // hole matches this page's first packet in the loop below and reports the
                    // packet's true start granule, which the caller clamps forward to.
                }
                else
                {
                    // backward calculation over-counted samples (e.g., EOS-clipped last page);
                    // shift gps up to align with the known previous-page boundary
                    for (var i = 0; i < gps.Length; i++)
                    {
                        gps[i] -= diff;
                    }
                }
            }

            // finally, find the packet with the requested granulePos
            for (var i = 0; i < gps.Length; i++)
            {
                if (gps[i] >= granulePos)
                {
                    granulePos = i == 0 ? endGP : gps[i - 1];
                    return i;
                }
            }

            throw new System.IO.InvalidDataException("Could not find seek packet?!");
        }

        private int FindPacket(int pageIndex, int preRoll, ref long granulePos, GetPacketGranuleCount getPacketGranuleCount)
        {
            // pageIndex is _probably_ the correct page (bugs in libogg mean long->short over page boundary isn't always correct).
            // We check for this by looking for a difference in the previous page's granulePos vs. the calculated value

            // first we look at the page info to see how it is set up
            var (lastPageGranulePos, lastPagePacketLength, firstRealPacket) = GetPreviousPageInfo(pageIndex, getPacketGranuleCount);

            // now get the info on the target page
            var (gps, endGP) = GetTargetPageInfo(pageIndex, firstRealPacket, lastPagePacketLength, getPacketGranuleCount);

            // finally figure out which packet in our known info we need to use
            var packetIndex = FindPacket(gps, endGP, lastPageGranulePos, lastPagePacketLength, ref granulePos);

            // then apply the preRoll (but only if we're not seeking into the first packet, which is its own preRoll)
            if (endGP > 0 || packetIndex > 1)
            {
                packetIndex -= preRoll;
            }
            return packetIndex;
        }

        // this method calc's the appropriate page and packet prior to the one specified, honoring continuations and handling negative packetIndex values
        // if packet index is larger than the current page allows, we just return it as-is
        private bool NormalizePacketIndex(ref int pageIndex, ref int packetIndex)
        {
            if (!_reader.GetPage(pageIndex, out _, out var isResync, out var isContinuation, out _, out _, out _))
            {
                return false;
            }

            var pgIdx = pageIndex;
            var pktIdx = packetIndex;
            var firstDataPage = _reader.FirstDataPageIndex;

            while (pktIdx < (isContinuation ? 1: 0))
            {
                // can't merge across resync
                if (isContinuation && isResync) return false;

                // walked back to the first audio page without resolving — snap to stream
                // beginning, consistent with the SeekTo first-page shortcut. Without this bound, a
                // pathological one-continued-packet-per-page stream is an O(N) walk that only ends when
                // GetPage throws on a negative index; the bound makes it an O(1) snap.
                if (pgIdx <= firstDataPage)
                {
                    pageIndex = firstDataPage;
                    packetIndex = _firstAudioPacketIndex;
                    return true;
                }

                // get the previous packet
                var wasContinuation = isContinuation;
                if (!_reader.GetPage(--pgIdx, out _, out isResync, out isContinuation, out var isContinued, out var packetCount, out _))
                {
                    return false;
                }

                // can't merge if continuation flags don't match
                if (wasContinuation && !isContinued) return false;

                // add the previous packet's packetCount
                pktIdx += packetCount - (wasContinuation ? 1 : 0);
            }

            pageIndex = pgIdx;
            packetIndex = pktIdx;
            return true;
        }

        private Packet GetNextPacket(ref int pageIndex, ref int packetIndex)
        {
            if (_reader == null) throw new ObjectDisposedException(nameof(PacketProvider));

            if (_lastPacketPacketIndex != packetIndex || _lastPacketPageIndex != pageIndex || _lastPacket == null)
            {
                _lastPacket = null;

                while (_reader.GetPage(pageIndex, out var granulePos, out var isResync, out _, out var isContinued, out var packetCount, out var pageOverhead))
                {
                    _lastPacketPageIndex = pageIndex;
                    _lastPacketPacketIndex = packetIndex;
                    _lastPacket = CreatePacket(ref pageIndex, ref packetIndex, true, granulePos, isResync, isContinued, packetCount, pageOverhead);
                    _nextPacketPageIndex = pageIndex;
                    _nextPacketPacketIndex = packetIndex;
                    break;
                }
            }
            else
            {
                pageIndex = _nextPacketPageIndex;
                packetIndex = _nextPacketPacketIndex;
            }
            return _lastPacket;
        }

        private Packet CreatePacket(ref int pageIndex, ref int packetIndex, bool advance, long granulePos, bool isResync, bool isContinued, int packetCount, int pageOverhead)
        {
            // save off the packet data for the initial packet
            var firstPacketData = _reader.GetPagePackets(pageIndex)[packetIndex];
            var firstPart = (pageIndex << 8) | packetIndex;

            // make sure we handle continuations
            bool isLastPacket;
            bool isFirstPacket;
            var finalPage = pageIndex;
            int[] extraParts = null;

            if (isContinued && packetIndex == packetCount - 1)
            {
                // by definition, it's the first packet in the page it ends on
                isFirstPacket = true;

                // but we don't want to include the current page's overhead if we didn't start the page
                if (packetIndex > 0)
                {
                    pageOverhead = 0;
                }

                // go read the next page(s) that include this packet
                var contPageIdx = pageIndex;
                List<int> extraList = null;
                while (isContinued)
                {
                    if (!_reader.GetPage(++contPageIdx, out granulePos, out isResync, out var isContinuation, out isContinued, out packetCount, out var contPageOverhead))
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

                    // if the next page is continued, only keep reading if there are no more packets in the page
                    if (isContinued && packetCount > 1)
                    {
                        isContinued = false;
                    }

                    // track the continuation page
                    if (extraList == null) extraList = new List<int>();
                    extraList.Add(contPageIdx << 8);
                }

                extraParts = extraList?.ToArray();

                // we're now the first packet in the final page, so we'll act like it...
                isLastPacket = packetCount == 1;

                // track the final page read
                finalPage = contPageIdx;
            }
            else
            {
                isFirstPacket = packetIndex == 0;
                isLastPacket = packetIndex == packetCount - 1;
            }

            // create the packet instance — avoid List<int> allocation for the common single-page case
            var packet = extraParts != null
                ? new Packet(firstPart, extraParts, this, firstPacketData)
                : new Packet(firstPart, this, firstPacketData);
            packet.IsResync = isResync;

            // if it's the first packet, associate the container overhead with it
            if (isFirstPacket)
            {
                packet.ContainerOverheadBits = pageOverhead * 8;
            }

            // if we're the last packet completed in the page, set the .GranulePosition
            if (isLastPacket)
            {
                packet.GranulePosition = granulePos;

                // if we're the last packet completed in the page, no more pages are available, and _hasAllPages is set, set .IsEndOfStream
                if (_reader.HasAllPages && finalPage == _reader.PageCount - 1)
                {
                    packet.IsEndOfStream = true;
                }
            }

            if (advance)
            {
                // if we've advanced a page, we continued a packet and should pick up with the next page
                if (finalPage != pageIndex)
                {
                    // we're on the final page now
                    pageIndex = finalPage;

                    // the packet index will be modified below, so set it to the end of the continued packet
                    packetIndex = 0;
                }

                // if we're on the last packet in the page, move to the next page
                // we can't use isLast here because the logic is different; last in page granule vs. last in physical page
                if (packetIndex == packetCount - 1)
                {
                    ++pageIndex;
                    packetIndex = 0;
                }
                // otherwise, just move to the next packet
                else
                {
                    ++packetIndex;
                }
            }

            // done!
            return packet;
        }

        Memory<byte> IPacketReader.GetPacketData(int pagePacketIndex)
        {
            var pageIndex = (pagePacketIndex >> 8) & 0xFFFFFF;
            var packetIndex = pagePacketIndex & 0xFF;

            var packets = _reader.GetPagePackets(pageIndex);
            if (packetIndex < packets.Length)
            {
                return packets[packetIndex];
            }
            return Memory<byte>.Empty;
        }

        void IPacketReader.InvalidatePacketCache(IPacket packet)
        {
            if (ReferenceEquals(_lastPacket, packet))
            {
                _lastPacket = null;
            }
        }
    }
}
