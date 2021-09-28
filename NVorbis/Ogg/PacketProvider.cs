using NVorbis.Contracts;
using NVorbis.Contracts.Ogg;
using System;
using System.Collections.Generic;

namespace NVorbis.Ogg
{
    class PacketProvider : Contracts.IPacketProvider, IPacketReader
    {
        private IStreamPageReader _reader;

        private int _pageIndex;
        private int _packetIndex;

        private int _lastPacketPageIndex;
        private int _lastPacketPacketIndex;
        private Packet _lastPacket;
        private int _nextPacketPageIndex;
        private int _nextPacketPacketIndex;

        public bool CanSeek => true;

        public int StreamSerial { get; }

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

            int pageIndex;
            int packetIndex;
            if (granulePos == 0)
            {
                // for this, we can generically say the first packet on the first page having a non-zero granule
                pageIndex = _reader.FirstDataPageIndex;
                packetIndex = 0;
            }
            else
            {
                pageIndex = _reader.FindPage(granulePos);
                if (_reader.HasAllPages && _reader.MaxGranulePosition == granulePos)
                {
                    // allow seek to the offset immediatly after the last available (for what good it'll do)
                    _lastPacket = null;
                    _pageIndex = pageIndex;
                    _packetIndex = 0;
                    return granulePos;
                }
                else
                {
                    packetIndex = FindPacket(pageIndex, ref granulePos, getPacketGranuleCount);
                }
                packetIndex -= preRoll;
            }

            if (!NormalizePacketIndex(ref pageIndex, ref packetIndex))
            {
                throw new ArgumentOutOfRangeException(nameof(granulePos));
            }

            if (pageIndex < _reader.FirstDataPageIndex)
            {
                pageIndex = _reader.FirstDataPageIndex;
                packetIndex = 0;
            }

            _lastPacket = null;
            _pageIndex = pageIndex;
            _packetIndex = packetIndex;
            return granulePos;
        }

        private int FindPacket(int pageIndex, ref long granulePos, GetPacketGranuleCount getPacketGranuleCount)
        {
            // pageIndex is the correct page; we just need to figure out which packet
            bool isContinued;
            int firstRealPacket = 0;
            if (_reader.GetPage(pageIndex - 1, out _, out _, out _, out isContinued, out _, out _))
            {
                if (isContinued)
                {
                    firstRealPacket = 1;
                }
            }
            else
            {
                throw new System.IO.InvalidDataException("Could not get page?!");
            }

            // now get the ending granule of the page
            if (!_reader.GetPage(pageIndex, out var pageGranulePos, out var isResync, out var isContinuation, out isContinued, out var packetCount, out _))
            {
                throw new System.IO.InvalidDataException("Could not get found page?!");
            }

            if (isContinued)
            {
                // if continued, the last packet index doesn't apply
                packetCount--;
            }

            var packetIndex = packetCount;
            var isLastInPage = !isContinued;
            var endGP = pageGranulePos;
            while (endGP > granulePos && --packetIndex >= firstRealPacket)
            {
                // it would be nice to pass false instead of isContinued, but (hypothetically) we don't know if getPacketGranuleCount(...) needs the whole thing...
                // Vorbis doesn't, but someone might decide to try to use us for another purpose so we'll be good here.
                var packet = CreatePacket(ref pageIndex, ref packetIndex, false, pageGranulePos, packetIndex == 0 && isResync, isContinued, packetCount, 0);
                if (packet == null)
                {
                    throw new System.IO.InvalidDataException("Could not find end of continuation!");
                }
                endGP -= getPacketGranuleCount(packet, isLastInPage);
                isLastInPage = false;
            }

            if (packetIndex < firstRealPacket)
            {
                // either it's a continued packet OR we've hit the "long->short over page boundary" bug.
                // in both cases we'll just return the last packet of the previous page.
                var prevPageIndex = pageIndex;
                var prevPacketIndex = -1;
                if (!NormalizePacketIndex(ref prevPageIndex, ref prevPacketIndex))
                {
                    throw new System.IO.InvalidDataException("Failed to normalize packet index?");
                }
                var packet = CreatePacket(ref prevPageIndex, ref prevPacketIndex, false, endGP, false, isContinuation, prevPacketIndex + 1, 0);
                if (packet == null)
                {
                    throw new System.IO.InvalidDataException("Could not load previous packet!");
                }
                granulePos = endGP - getPacketGranuleCount(packet, false);
                return -1;
            }

            // normal seek; that wasn't so hard, right?
            granulePos = endGP;
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

            while (pktIdx < (isContinuation ? 1: 0))
            {
                // can't merge across resync
                if (isContinuation && isResync) return false;

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

            // create the packet list and add the item to it
            var pktList = new List<int>(2) { (pageIndex << 8) | packetIndex };

            // make sure we handle continuations
            bool isLastPacket;
            bool isFirstPacket;
            var finalPage = pageIndex;
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

                    // add the packet to the list
                    pktList.Add(contPageIdx << 8);
                }

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

            // create the packet instance and populate it with the appropriate initial data
            var packet = new Packet(pktList, this, firstPacketData)
            {
                IsResync = isResync,
            };

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
