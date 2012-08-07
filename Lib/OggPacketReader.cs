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
        bool _eosFound;

        int _dataStartPacketIndex;

        Queue<OggPacket> _packetQueue;
        List<OggPacket> _packetList;

        List<int> _pageSeqNos;

        internal OggPacketReader(OggContainerReader container, int streamSerial)
        {
            _container = container;
            _streamSerial = streamSerial;

            _packetQueue = new Queue<OggPacket>();
            _packetList = new List<OggPacket>();

            _pageSeqNos = new List<int>();
        }

        internal void AddPacket(OggPacket packet)
        {
            if (!_pageSeqNos.Contains(packet.PageSequenceNumber)) _pageSeqNos.Add(packet.PageSequenceNumber);

            _eosFound |= packet.IsEndOfStream;
            _packetQueue.Enqueue(packet);
        }

        internal void SetDataStart()
        {
            _dataStartPacketIndex = _packetList.Count;
        }

        void GetMorePackets()
        {
            // gather pages until we have packets or we've found the end of our stream...
            while (!_eosFound && _packetQueue.Count == 0) _container.GatherNextPage(_streamSerial);

            // if the queue is still empty, we're done...
            if (_packetQueue.Count == 0) throw new EndOfStreamException();
        }

        void MergeQueuePackets()
        {
            // if the queue is empty, we have nothing to do...
            if (_packetQueue.Count == 0) return;

            // go through the queue and merge all continuations...
            var list = _packetQueue.ToArray();

            var idx = Array.FindIndex(list, p => p.IsContinued);
            if (idx > -1)
            {
                // if the last packet is a continuation, get more packets first...
                while (list[list.Length - 1].IsContinued)
                {
                    if (_eosFound)
                    {
                        // the continuation flag must be wrong, or we have a partial stream...  ignore the flag...
                        list[list.Length - 1].IsContinued = false;
                        break;
                    }
                    else
                    {
                        // we're not at the end of the stream, so get more data and try again...
                        GetMorePackets();
                        list = _packetQueue.ToArray();
                    }
                }

                // clear the queue as we will be repopulating it momentarily...
                _packetQueue.Clear();
                for (int i = 0; i < idx; i++)
                {
                    _packetQueue.Enqueue(list[i]);
                }

                var mergeTarget = idx;
                while (++idx < list.Length)
                {
                    if (list[idx].IsResync || list[idx].IsFresh)
                    {
                        // we can't merge...  cancel the merge target
                        mergeTarget = -1;
                    }

                    if (mergeTarget > -1)
                    {
                        list[mergeTarget].AddNextPage(list[idx].Offset, list[idx].Length);
                        if (!list[idx].IsContinued) mergeTarget = -1;
                    }
                    else
                    {
                        _packetQueue.Enqueue(list[idx]);
                        if (list[idx].IsContinued)
                        {
                            mergeTarget = idx;
                            list[idx].IsContinued = false;
                        }
                    }
                }
            }
        }

        internal OggPacket GetNextPacket()
        {
            // make sure we have some packets in the queue...
            GetMorePackets();
            MergeQueuePackets();

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
                MergeQueuePackets();

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

        internal int GetPageCount()
        {
            return _pageSeqNos.Count;
        }
    }
}
