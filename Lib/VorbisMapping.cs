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
    abstract class VorbisMapping
    {
        internal static VorbisMapping Init(VorbisReader vorbis, OggPacket reader)
        {
            var type = (int)reader.ReadBits(16);

            VorbisMapping mapping = null;
            switch (type)
            {
                case 0: mapping = new Mapping0(vorbis); break;
            }
            if (mapping == null) throw new InvalidDataException();

            mapping.Init(reader);
            return mapping;
        }

        VorbisReader _vorbis;

        protected VorbisMapping(VorbisReader vorbis)
        {
            _vorbis = vorbis;
        }

        abstract protected void Init(OggPacket reader);

        internal Submap[] Submaps;

        internal Submap[] ChannelSubmap;

        internal CouplingStep[] CouplingSteps;

        class Mapping0 : VorbisMapping
        {
            internal Mapping0(VorbisReader vorbis) : base(vorbis) { }

            protected override void Init(OggPacket reader)
            {
                var submapCount = 1;
                if (reader.ReadBit()) submapCount += (int)reader.ReadBits(4);

                // square polar mapping
                var couplingSteps = 0;
                if (reader.ReadBit())
                {
                    couplingSteps = (int)reader.ReadBits(8) + 1;
                }

                var couplingBits = Utils.ilog(_vorbis._channels - 1);
                CouplingSteps = new CouplingStep[couplingSteps];
                for (int j = 0; j < couplingSteps; j++)
                {
                    var magnitude = (int)reader.ReadBits(couplingBits);
                    var angle = (int)reader.ReadBits(couplingBits);
                    if (magnitude == angle || magnitude > _vorbis._channels - 1 || angle > _vorbis._channels - 1)
                        throw new InvalidDataException();
                    CouplingSteps[j] = new CouplingStep { Angle = angle, Magnitude = magnitude };
                }

                // reserved bits
                if (reader.ReadBits(2) != 0UL) throw new InvalidDataException();

                // channel multiplex
                var mux = new int[_vorbis._channels];
                if (submapCount > 1)
                {
                    for (int c = 0; c < ChannelSubmap.Length; c++)
                    {
                        mux[c] = (int)reader.ReadBits(4);
                        if (mux[c] >= submapCount) throw new InvalidDataException();
                    }
                }

                // submaps
                Submaps = new Submap[submapCount];
                for (int j = 0; j < submapCount; j++)
                {
                    reader.ReadBits(8); // unused placeholder
                    var floorNum = (int)reader.ReadBits(8);
                    if (floorNum >= _vorbis.Floors.Length) throw new InvalidDataException();
                    var residueNum = (int)reader.ReadBits(8);
                    if (residueNum >= _vorbis.Residues.Length) throw new InvalidDataException();

                    Submaps[j] = new Submap
                    {
                        Floor = _vorbis.Floors[floorNum],
                        Residue = _vorbis.Residues[floorNum]
                    };
                }

                ChannelSubmap = new Submap[_vorbis._channels];
                for (int c = 0; c < ChannelSubmap.Length; c++)
                {
                    ChannelSubmap[c] = Submaps[mux[c]];
                }
            }
        }

        internal class Submap
        {
            internal Submap() { }

            internal VorbisFloor Floor;
            internal VorbisResidue Residue;
        }

        internal class CouplingStep
        {
            internal CouplingStep() { }

            internal int Magnitude;
            internal int Angle;
        }
    }
}
