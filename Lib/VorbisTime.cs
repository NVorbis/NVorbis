/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2012, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * See COPYING for license terms (LGPL).                                    *
 *                                                                          *
 ***************************************************************************/
using System.IO;

namespace NVorbis
{
    abstract class VorbisTime
    {
        internal static VorbisTime Init(VorbisReader vorbis, OggPacket reader)
        {
            var type = (int)reader.ReadBits(16);

            VorbisTime time = null;
            switch (type)
            {
                case 0: time = new Time0(vorbis); break;
            }
            if (time == null) throw new InvalidDataException();

            time.Init(reader);
            return time;
        }

        VorbisReader _vorbis;

        protected VorbisTime(VorbisReader vorbis)
        {
            _vorbis = vorbis;
        }

        abstract protected void Init(OggPacket reader);

        class Time0 : VorbisTime
        {
            internal Time0(VorbisReader vorbis) : base(vorbis) { }

            protected override void Init(OggPacket reader)
            {
                
            }
        }
    }
}
