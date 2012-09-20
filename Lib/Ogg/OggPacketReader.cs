/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2012, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * See COPYING for license terms (Ms-PL).                                   *
 *                                                                          *
 ***************************************************************************/
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace NVorbis.Ogg
{
    class PacketReader
    {
        ContainerReader _container;
        int _streamSerial;
        bool _eosFound;

        Packet _first, _current, _last;

        internal PacketReader(ContainerReader container, int streamSerial)
        {
            _container = container;
            _streamSerial = streamSerial;
        }

        internal void AddPacket(DataPacket packet)
        {
            // if the packet is a resync, it cannot be a continuation...
            if (packet.IsResync)
            {
                packet.IsContinuation = false;
                if (_last != null) _last.IsContinued = false;
            }

            if (packet.IsContinuation)
            {
                // if we get here, the stream is invalid if there isn't a previous packet
                if (_last == null) throw new InvalidDataException();

                // if the last packet isn't continued, something is wrong
                if (!_last.IsContinued) throw new InvalidDataException();

                _last.MergeWith(packet);
                _last.IsContinued = packet.IsContinued;
            }
            else
            {
                var p = packet as Packet;
                if (p == null) throw new ArgumentException("Wrong packet datatype", "packet");

                if (_first == null)
                {
                    // this is the first packet to add, so just set first & last to point at it
                    _first = p;
                    _last = p;
                }
                else
                {
                    // swap the new packet in to the last position (remember, we're doubly-linked)
                    _last = ((p.Prev = _last).Next = p);
                }
            }

            _eosFound |= packet.IsEndOfStream;
        }

        void GetMorePackets()
        {
            // tell our container we need another page...  unless we've found the end of our stream.
            if (!_eosFound) _container.GatherNextPage(_streamSerial);
        }

        internal DataPacket GetNextPacket()
        {
            // "current" is always set to the packet previous to the one about to be returned...

            while (_last == null || _last.IsContinued || _current == _last)
            {
                GetMorePackets();

                // if we've read the entire stream, do some further checking...
                if (_eosFound)
                {
                    // make sure the last packet read isn't continued... (per the spec, if the last packet is a partial, ignore it)
                    // if _last is null, something has gone horribly wrong (i.e., that shouldn't happen)
                    if (_last.IsContinued)
                    {
                        _last = _last.Prev;
                        _last.Next.Prev = null;
                        _last.Next = null;
                    }

                    // if our "current" packet is the same as the "last" packet, we're done
                    // _last won't be null here
                    if (_current == _last) throw new EndOfStreamException();
                }
            }

            DataPacket packet;
            if (_current == null)
            {
                packet = (_current = _first);
            }
            else
            {
                packet = (_current = _current.Next);
            }

            if (packet.IsContinued) throw new InvalidDataException();

            // make sure the packet is ready for "playback"
            packet.Reset();

            return packet;
        }

        internal void SeekToPacket(int index)
        {
            _current = GetPacketByIndex(index).Prev;
        }

        Packet GetPacketByIndex(int index)
        {
            if (index < 0) throw new ArgumentOutOfRangeException("index");

            while (_first == null)
            {
                if (_eosFound) throw new InvalidDataException();

                GetMorePackets();
            }

            var packet = _first;
            while (--index >= 0)
            {
                while (packet.Next == null)
                {
                    if (_eosFound) throw new ArgumentOutOfRangeException("index");

                    GetMorePackets();
                }

                packet = packet.Next;
            }

            return packet;
        }

        internal void ReadAllPages()
        {
            while (!_eosFound)
            {
                _container.GatherNextPage(_streamSerial);
            }
        }

        internal DataPacket GetLastPacket()
        {
            ReadAllPages();

            return _last;
        }

        internal int GetTotalPageCount()
        {
            ReadAllPages();

            // here we just count the number of times the page sequence number changes
            var cnt = 0;
            var lastPageSeqNo = 0;
            var packet = _first;
            while (packet != null)
            {
                if (packet.PageSequenceNumber != lastPageSeqNo)
                {
                    ++cnt;
                    lastPageSeqNo = packet.PageSequenceNumber;
                }
                packet = packet.Next;
            }
            return cnt;
        }

        internal DataPacket GetPacket(int packetIndex)
        {
            var packet = GetPacketByIndex(packetIndex);
            packet.Reset();
            return packet;
        }

        internal int FindPacket(long granulePos, Func<DataPacket, DataPacket, DataPacket, int> packetGranuleCountCallback)
        {
            // This will find which packet contains the granule position being requested.  It is basically a linear search.
            // Please note, the spec actually calls for a bisection search, but the result here should be the same.

            // don't look for any position before 0!
            if (granulePos < 0) throw new ArgumentOutOfRangeException("granulePos");

            // find the first packet with a higher GranulePosition than the requested value
            // this is safe to do because we'll get a whole page at a time...
            while (_last == null || _last.PageGranulePosition < granulePos)
            {
                if (_eosFound)
                {
                    // only throw an exception when our data is no good
                    if (_first == null)
                    {
                        throw new InvalidDataException();
                    }
                    return -1;
                }

                GetMorePackets();
            }

            // We now know the page of the last packet ends somewhere past the requested granule position...
            // search back until we find the first packet past the requested position
            // if we make it back to the beginning, return -1;

            var packet = _last;
            // if the last packet is continued, ignore it (the page granule count actually applies to the previous packet)
            if (packet.IsContinued)
            {
                packet = packet.Prev;
            }
            do
            {
                // if we don't have a granule count, it's a new packet and we need to calculate its count & position
                if (!packet.GranuleCount.HasValue)
                {
                    // fun part... make sure the packets are ready for "playback"
                    if (packet.Prev != null) packet.Prev.Reset();
                    packet.Reset();
                    if (packet.Next != null) packet.Next.Reset();

                    // go ask the callback to calculate the granule count for this packet (given the surrounding packets)
                    packet.GranuleCount = packetGranuleCountCallback(packet.Prev, packet, packet.Next);

                    // Something to think about...  Every packet that "ends" a page could have its granule position set directly...
                    //   We don't do that because this does the job just as well, but maybe it would be a good idea?

                    // if it's the last (or second-last in the stream) packet, or it's "Next" is continued, just use the page granule position
                    if (packet == _last || (_eosFound && packet == _last.Prev) || packet.Next.IsContinued)
                    {
                        // if the page's granule position is -1, something must be horribly wrong... (AddPacket should have addressed this above)
                        if (packet.PageGranulePosition == -1) throw new InvalidDataException();

                        // use the page's granule position
                        packet.GranulePosition = packet.PageGranulePosition;

                        // if it's the last packet in the stream, it's a partial...
                        if (packet == _last && _eosFound)
                        {
                            packet.GranuleCount = packet.PageGranulePosition - packet.Prev.PageGranulePosition;
                        }
                    }
                    else
                    {
                        // this packet's granule position is the next packet's position less the next packet's count (which should already be calculated)
                        packet.GranulePosition = packet.Next.GranulePosition - packet.Next.GranuleCount.Value;
                    }
                }

                // now we know what this packet's granule position is...
                if (packet.GranulePosition < granulePos)
                {
                    // we've found the packet previous to the one we need...
                    packet = packet.Next;
                    break;
                }

                // we didn't find the packet, so update and loop
                packet = packet.Prev;
            } while (packet != null);

            // if we didn't find the packet, something is wrong
            if (packet == null) return -1;

            // we found the packet, so now we just have to count back to the beginning and see what its index is...
            int idx = 0;
            while (packet.Prev != null)
            {
                packet = packet.Prev;
                ++idx;
            }
            return idx;
        }
    }
}
