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

namespace NVorbis
{
    class OggPacketReader
    {
        OggContainerReader _container;
        int _streamSerial;
        bool _eosFound, _runMerge;

        int _dataStartPacketIndex;

        Queue<OggPacket> _packetQueue, _tempQueue;
        List<OggPacket> _packetList;

        internal OggPacketReader(OggContainerReader container, int streamSerial)
        {
            _container = container;
            _streamSerial = streamSerial;

            _packetQueue = new Queue<OggPacket>();
            _tempQueue = new Queue<OggPacket>();
            _packetList = new List<OggPacket>();
        }

        internal void AddPacket(OggPacket packet)
        {
            _runMerge |= packet.IsContinuation;
            _eosFound |= packet.IsEndOfStream;
            _packetQueue.Enqueue(packet);
        }

        internal void SetDataStart()
        {
            _dataStartPacketIndex = _packetList.Count;
        }

        void GetMorePackets()
        {
            // gather pages until we have more packets or we've found the end of our stream...
            var count = _packetQueue.Count;
            while (!_eosFound && _packetQueue.Count == count) _container.GatherNextPage(_streamSerial);
        }

        void MergeQueuePackets()
        {
            // if the queue is empty, we have nothing to do...
            if (_packetQueue.Count == 0) return;

            // try to merge things in
            OggPacket lastPacket = null;
            while (_packetQueue.Count > 0)
            {
                // get the next packet and queue it up
                var packet = _packetQueue.Dequeue();

                if (!packet.IsResync && packet.IsContinuation)
                {
                    if (lastPacket == null) throw new InvalidDataException();

                    // continue the last packet
                    lastPacket.MergeWith(packet);
                }
                else
                {
                    _tempQueue.Enqueue(packet);
                    lastPacket = packet;
                }
            }
            var temp = _packetQueue;
            _packetQueue = _tempQueue;
            _tempQueue = temp;

            _runMerge = false;
        }

        internal OggPacket GetNextPacket()
        {
            // make sure we have some packets in the queue...
            if (_packetQueue.Count <= 1)    // never wait until the last packet... makes merges happen correctly...
            {
                GetMorePackets();

                if (_packetQueue.Count == 0) throw new EndOfStreamException();
            }

            if (_runMerge) MergeQueuePackets();

            // get the next packet in the queue...
            var packet = _packetQueue.Dequeue();

            // save off the packet for later (in case we start seeking around)
            _packetList.Add(packet);

            return packet;
        }

        internal void SeekToPacket(int index)
        {
            if (index < 0) throw new ArgumentOutOfRangeException("index");

            // make sure we've merged-up everything...
            MergeQueuePackets();

            if (index >= _packetList.Count + _packetQueue.Count) throw new ArgumentOutOfRangeException("index");

            // add the queued packets to the list...
            _packetList.AddRange(_packetQueue);

            // clear the queue (for now)
            _packetQueue.Clear();

            // re-queue the packets...
            for (; index < _packetList.Count; index++)
            {
                _packetQueue.Enqueue(_packetList[index]);
            }
        }

        internal void SeekToGranule(long position)
        {
            if (position < 0L) throw new ArgumentOutOfRangeException("position");

            // go through the list until we find a GranulePosition higher than the requested one...
            // if GranulePosition == 0, look for the first PageGranulePosition higher than the requested one, then back up 1 page...

            // first, make sure we have the ability to satisfy the request from the cached list
            while (position > _packetList[_packetList.Count - 1].PageGranulePosition)
            {
                if (_eosFound) throw new ArgumentOutOfRangeException("position");

                GetMorePackets();
                if (_runMerge) MergeQueuePackets();

                if (_packetQueue.Count == 0) throw new EndOfStreamException();

                _packetList.AddRange(_packetQueue);
                _packetQueue.Clear();
            }

            // second, find the last packet where the page granule position is less than or equal to the position
            int idx = _packetList.Count;
            while (_packetList[--idx].PageGranulePosition > position)
            {
                if (idx == 0) throw new InvalidOperationException("Could not find requested position.");
            }

            // _packetList[idx + 1].PageGranulePosition > position
            // _packetList[idx].PageGranulePosition <= position
            // if ==, the packet *ends* on the requested granule position
            // if <, the packet ends before the granule position

            if (_packetList[idx].PageGranulePosition == position)
            {
                ++idx;
            }
            else
            {
                // third, try to find the exact packet where the position exists...
                while (idx < _packetList.Count)
                {
                    var gp = _packetList[idx + 1].GranulePosition;
                    if (gp == 0) break; // we can't go any further
                    if (gp > position) break;   // we've found the right packet

                    ++idx;  // try again...
                }
            }

            if (idx == _packetList.Count) throw new InvalidOperationException("Could not find requested position.");

            SeekToPacket(idx);
        }

        internal void ReadAllPages()
        {
            while (!_eosFound)
            {
                _container.GatherNextPage(_streamSerial);
            }

            MergeQueuePackets();
        }

        internal OggPacket GetLastPacket()
        {
            ReadAllPages();

            return _packetQueue.LastOrDefault() ?? _packetList.Last();
        }
    }
}
