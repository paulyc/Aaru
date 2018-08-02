﻿// /***************************************************************************
// The Disc Image Chef
// ----------------------------------------------------------------------------
//
// Filename       : ClauniaSubchannelTransform.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : Disk image plugins.
//
// --[ Description ] ----------------------------------------------------------
//
//     Contains the CD ECC algorithm.
//
// --[ License ] --------------------------------------------------------------
//
//     This library is free software; you can redistribute it and/or modify
//     it under the terms of the GNU Lesser General Public License as
//     published by the Free Software Foundation; either version 2.1 of the
//     License, or (at your option) any later version.
//
//     This library is distributed in the hope that it will be useful, but
//     WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
//     Lesser General Public License for more details.
//
//     You should have received a copy of the GNU Lesser General Public
//     License along with this library; if not, see <http://www.gnu.org/licenses/>.
//
// ----------------------------------------------------------------------------
// Copyright © 2011-2018 Natalia Portillo
// ECC algorithm from ECM(c) 2002-2011 Neill Corlett
// ****************************************************************************/

using System;
using DiscImageChef.CommonTypes.Enums;

namespace DiscImageChef.DiscImages
{
    public partial class DiscImageChef
    {
        byte[] eccBTable;
        byte[] eccFTable;
        uint[] edcTable;

        void EccInit()
        {
            eccFTable = new byte[256];
            eccBTable = new byte[256];
            edcTable  = new uint[256];

            for(uint i = 0; i < 256; i++)
            {
                uint edc = i;
                uint j   = (uint)((i << 1) ^ ((i & 0x80) == 0x80 ? 0x11D : 0));
                eccFTable[i]     = (byte)j;
                eccBTable[i ^ j] = (byte)i;
                for(j = 0; j < 8; j++) edc = (edc >> 1) ^ ((edc & 1) > 0 ? 0xD8018001 : 0);
                edcTable[i] = edc;
            }
        }

        bool SuffixIsCorrect(byte[] sector)
        {
            if(sector[0x814] != 0x00 || // reserved (8 bytes)
               sector[0x815] != 0x00 || sector[0x816] != 0x00 || sector[0x817] != 0x00 || sector[0x818] != 0x00 ||
               sector[0x819] != 0x00 || sector[0x81A] != 0x00 || sector[0x81B] != 0x00) return false;

            byte[] address = new byte[4];
            byte[] data    = new byte[2060];
            byte[] data2   = new byte[2232];
            byte[] eccP    = new byte[172];
            byte[] eccQ    = new byte[104];

            Array.Copy(sector, 0x0C,  address, 0, 4);
            Array.Copy(sector, 0x10,  data,    0, 2060);
            Array.Copy(sector, 0x10,  data2,   0, 2232);
            Array.Copy(sector, 0x81C, eccP,    0, 172);
            Array.Copy(sector, 0x8C8, eccQ,    0, 104);

            bool correctEccP = CheckEcc(ref address, ref data, 86, 24, 2, 86, ref eccP);
            if(!correctEccP) return false;

            bool correctEccQ = CheckEcc(ref address, ref data2, 52, 43, 86, 88, ref eccQ);
            if(!correctEccQ) return false;

            uint storedEdc              = BitConverter.ToUInt32(sector, 0x810);
            uint edc                    = 0;
            int  size                   = 0x810;
            int  pos                    = 0;
            for(; size > 0; size--) edc = (edc >> 8) ^ edcTable[(edc ^ sector[pos++]) & 0xFF];
            uint calculatedEdc          = edc;

            return calculatedEdc == storedEdc;
        }

        bool CheckEcc(ref byte[] address,  ref byte[] data, uint majorCount, uint minorCount, uint majorMult,
                      uint       minorInc, ref byte[] ecc)
        {
            uint size = majorCount * minorCount;
            uint major;
            for(major = 0; major < majorCount; major++)
            {
                uint idx  = (major >> 1) * majorMult + (major & 1);
                byte eccA = 0;
                byte eccB = 0;
                uint minor;
                for(minor = 0; minor < minorCount; minor++)
                {
                    byte temp = idx < 4 ? address[idx] : data[idx - 4];
                    idx += minorInc;
                    if(idx >= size) idx -= size;
                    eccA ^= temp;
                    eccB ^= temp;
                    eccA =  eccFTable[eccA];
                }

                eccA = eccBTable[eccFTable[eccA] ^ eccB];
                if(ecc[major] != eccA || ecc[major + majorCount] != (eccA ^ eccB)) return false;
            }

            return true;
        }

        void WriteEcc(byte[]     address, byte[] data, uint majorCount, uint minorCount, uint majorMult,
                      uint       minorInc,
                      ref byte[] ecc, int addressOffset, int dataOffset, int eccOffset)
        {
            uint size = majorCount * minorCount;
            uint major;
            for(major = 0; major < majorCount; major++)
            {
                uint idx  = (major >> 1) * majorMult + (major & 1);
                byte eccA = 0;
                byte eccB = 0;
                uint minor;
                for(minor = 0; minor < minorCount; minor++)
                {
                    byte temp = idx < 4 ? address[idx + addressOffset] : data[idx + dataOffset - 4];
                    idx += minorInc;
                    if(idx >= size) idx -= size;
                    eccA ^= temp;
                    eccB ^= temp;
                    eccA =  eccFTable[eccA];
                }

                eccA                                = eccBTable[eccFTable[eccA] ^ eccB];
                ecc[major              + eccOffset] = eccA;
                ecc[major + majorCount + eccOffset] = (byte)(eccA ^ eccB);
            }
        }

        void EccWriteSector(byte[] address, byte[] data, ref byte[] ecc, int addressOffset, int dataOffset,
                            int    eccOffset)
        {
            WriteEcc(address, data, 86, 24, 2,  86, ref ecc, addressOffset, dataOffset, eccOffset);        // P
            WriteEcc(address, data, 52, 43, 86, 88, ref ecc, addressOffset, dataOffset, eccOffset + 0xAC); // Q
        }

        static (byte minute, byte second, byte frame) LbaToMsf(long pos)
        {
            return ((byte)((pos + 150) / 75 / 60), (byte)((pos + 150) / 75 % 60), (byte)((pos + 150) % 75));
        }

        void ReconstructPrefix(ref byte[] sector, // must point to a full 2352-byte sector
                               TrackType  type,   long lba)
        {
            //
            // Sync
            //
            sector[0x000] = 0x00;
            sector[0x001] = 0xFF;
            sector[0x002] = 0xFF;
            sector[0x003] = 0xFF;
            sector[0x004] = 0xFF;
            sector[0x005] = 0xFF;
            sector[0x006] = 0xFF;
            sector[0x007] = 0xFF;
            sector[0x008] = 0xFF;
            sector[0x009] = 0xFF;
            sector[0x00A] = 0xFF;
            sector[0x00B] = 0x00;

            (byte minute, byte second, byte frame) msf = LbaToMsf(lba);

            sector[0x00C] = (byte)(((msf.minute / 10) << 4) + msf.minute % 10);
            sector[0x00D] = (byte)(((msf.second / 10) << 4) + msf.second % 10);
            sector[0x00E] = (byte)(((msf.frame  / 10) << 4) + msf.frame  % 10);

            switch(type)
            {
                case TrackType.CdMode1:
                    //
                    // Mode
                    //
                    sector[0x00F] = 0x01;
                    break;
                case TrackType.CdMode2Form1:
                case TrackType.CdMode2Form2:
                case TrackType.CdMode2Formless:
                    //
                    // Mode
                    //
                    sector[0x00F] = 0x02;
                    //
                    // Flags
                    //
                    sector[0x010] = sector[0x014];
                    sector[0x011] = sector[0x015];
                    sector[0x012] = sector[0x016];
                    sector[0x013] = sector[0x017];
                    break;
                default: return;
            }
        }

        void ReconstructEcc(ref byte[] sector, // must point to a full 2352-byte sector
                            TrackType  type)
        {
            byte[] computedEdc;

            switch(type)
            {
                //
                // Compute EDC
                //
                case TrackType.CdMode1:
                    computedEdc   = BitConverter.GetBytes(ComputeEdc(0, sector, 0x810));
                    sector[0x810] = computedEdc[0];
                    sector[0x811] = computedEdc[1];
                    sector[0x812] = computedEdc[2];
                    sector[0x813] = computedEdc[3];
                    break;
                case TrackType.CdMode2Form1:
                    computedEdc   = BitConverter.GetBytes(ComputeEdc(0, sector, 0x808, 0x10));
                    sector[0x818] = computedEdc[0];
                    sector[0x819] = computedEdc[1];
                    sector[0x81A] = computedEdc[2];
                    sector[0x81B] = computedEdc[3];
                    break;
                case TrackType.CdMode2Form2:
                    computedEdc   = BitConverter.GetBytes(ComputeEdc(0, sector, 0x91C, 0x10));
                    sector[0x92C] = computedEdc[0];
                    sector[0x92D] = computedEdc[1];
                    sector[0x92E] = computedEdc[2];
                    sector[0x92F] = computedEdc[3];
                    break;
                default: return;
            }

            byte[] zeroaddress = new byte[4];

            switch(type)
            {
                //
                // Compute ECC
                //
                case TrackType.CdMode1:
                    //
                    // Reserved
                    //
                    sector[0x814] = 0x00;
                    sector[0x815] = 0x00;
                    sector[0x816] = 0x00;
                    sector[0x817] = 0x00;
                    sector[0x818] = 0x00;
                    sector[0x819] = 0x00;
                    sector[0x81A] = 0x00;
                    sector[0x81B] = 0x00;
                    EccWriteSector(sector, sector, ref sector, 0xC, 0x10, 0x81C);
                    break;
                case TrackType.CdMode2Form1:
                    EccWriteSector(zeroaddress, sector, ref sector, 0, 0x10, 0x81C);
                    break;
                default: return;
            }

            //
            // Done
            //
        }

        uint ComputeEdc(uint edc, byte[] src, int size, int srcOffset = 0)
        {
            int pos                     = srcOffset;
            for(; size > 0; size--) edc = (edc >> 8) ^ edcTable[(edc ^ src[pos++]) & 0xFF];

            return edc;
        }
    }
}