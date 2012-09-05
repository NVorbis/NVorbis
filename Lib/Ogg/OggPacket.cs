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
using System.Text;
using System.IO;

namespace NVorbis.Ogg
{
    class Packet : DataPacket
    {
        Stream _stream;

        List<long> _offsets;
        List<int> _lengths;
        int _curIdx;
        int _curOfs;

        internal Packet(Stream stream, long startPos, int length)
            : base(length)
        {
            _stream = stream;

            _offsets = new List<long>();
            _lengths = new List<int>();
            _curIdx = 0;
            _curOfs = 0;

            _offsets.Add(startPos);
            _lengths.Add(length);
        }

        protected override void DoMergeWith(NVorbis.DataPacket continuation)
        {
            var op = continuation as Packet;

            if (op == null) throw new ArgumentException("Incorrect packet type!");

            _offsets.AddRange(op._offsets);
            _lengths.AddRange(op._lengths);

            Length += continuation.Length;

            // per the spec, a partial packet goes with the next page's granulepos.  we'll go ahead and assign it to the next page as well
            PageGranulePosition = continuation.PageGranulePosition;
            PageSequenceNumber = continuation.PageSequenceNumber;
        }

        protected override bool CanReset
        {
            get { return true; }
        }

        protected override void DoReset()
        {
            _curIdx = 0;
            _curOfs = 0;
            _stream.Position = _offsets[0];
        }

        protected override int ReadNextByte()
        {
            if (_curIdx == _offsets.Count) return -1;

            var pos = _offsets[_curIdx] + _curOfs;
            if (_stream.Position != pos) _stream.Seek(pos, SeekOrigin.Begin);
            var b = _stream.ReadByte();
            ++_curOfs;
            if (_curOfs >= _lengths[_curIdx])
            {
                ++_curIdx;
                _curOfs = 0;
            }
            return b;
        }
    }
}
