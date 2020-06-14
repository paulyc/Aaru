// /***************************************************************************
// Aaru Data Preservation Suite
// ----------------------------------------------------------------------------
//
// Filename       : Subchannel.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : CompactDisc dumping.
//
// --[ Description ] ----------------------------------------------------------
//
//     Handles CompactDisc subchannel data.
//
// --[ License ] --------------------------------------------------------------
//
//     This program is free software: you can redistribute it and/or modify
//     it under the terms of the GNU General Public License as
//     published by the Free Software Foundation, either version 3 of the
//     License, or (at your option) any later version.
//
//     This program is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU General Public License for more details.
//
//     You should have received a copy of the GNU General Public License
//     along with this program.  If not, see <http://www.gnu.org/licenses/>.
//
// ----------------------------------------------------------------------------
// Copyright © 2011-2020 Natalia Portillo
// ****************************************************************************/

using System;
using System.Collections.Generic;
using Aaru.Checksums;
using Aaru.CommonTypes.Enums;
using Aaru.CommonTypes.Extents;
using Aaru.CommonTypes.Structs;
using Aaru.Core.Logging;
using Aaru.Decoders.CD;
using Aaru.Devices;

// ReSharper disable JoinDeclarationAndInitializer
// ReSharper disable InlineOutVariableDeclaration
// ReSharper disable TooWideLocalVariableScope

namespace Aaru.Core.Devices.Dumping
{
    partial class Dump
    {
        public static bool SupportsRwSubchannel(Device dev, DumpLog dumpLog, UpdateStatusHandler updateStatus)
        {
            dumpLog?.WriteLine("Checking if drive supports full raw subchannel reading...");
            updateStatus?.Invoke("Checking if drive supports full raw subchannel reading...");

            return !dev.ReadCd(out _, out _, 0, 2352 + 96, 1, MmcSectorTypes.AllTypes, false, false, true,
                               MmcHeaderCodes.AllHeaders, true, true, MmcErrorField.None, MmcSubchannel.Raw,
                               dev.Timeout, out _);
        }

        public static bool SupportsPqSubchannel(Device dev, DumpLog dumpLog, UpdateStatusHandler updateStatus)
        {
            dumpLog?.WriteLine("Checking if drive supports PQ subchannel reading...");
            updateStatus?.Invoke("Checking if drive supports PQ subchannel reading...");

            return !dev.ReadCd(out _, out _, 0, 2352 + 16, 1, MmcSectorTypes.AllTypes, false, false, true,
                               MmcHeaderCodes.AllHeaders, true, true, MmcErrorField.None, MmcSubchannel.Q16,
                               dev.Timeout, out _);
        }

        // Return true if indexes have changed
        bool WriteSubchannelToImage(MmcSubchannel supportedSubchannel, MmcSubchannel desiredSubchannel, byte[] sub,
                                    ulong sectorAddress, uint length, SubchannelLog subLog,
                                    Dictionary<byte, string> isrcs, byte currentTrack, ref string mcn, Track[] tracks,
                                    ExtentsInt subchannelExtents)
        {
            if(supportedSubchannel == MmcSubchannel.Q16)
                sub = Subchannel.ConvertQToRaw(sub);

            if(!_fixSubchannelPosition &&
               desiredSubchannel != MmcSubchannel.None)
                _outputPlugin.WriteSectorsTag(sub, sectorAddress, length, SectorTagType.CdSectorSubchannel);

            subLog?.WriteEntry(sub, supportedSubchannel == MmcSubchannel.Raw, (long)sectorAddress, length);

            byte[] deSub = Subchannel.Deinterleave(sub);

            bool indexesChanged = CheckIndexesFromSubchannel(deSub, isrcs, currentTrack, ref mcn, tracks);

            if(!_fixSubchannelPosition ||
               desiredSubchannel == MmcSubchannel.None)
                return indexesChanged;

            int prePos = int.MinValue;

            // Check subchannel
            for(int subPos = 0; subPos < deSub.Length; subPos += 96)
            {
                long   lba    = (long)sectorAddress + (subPos / 96);
                bool   @fixed = false;
                byte[] q      = new byte[12];
                Array.Copy(deSub, subPos + 12, q, 0, 12);

                CRC16CCITTContext.Data(q, 10, out byte[] crc);
                bool crcOk = crc[0] == q[10] && crc[1] == q[11];

                bool pOk     = true;
                int  pWeight = 0;

                bool rwOk = true;

                for(int p = subPos; p < subPos + 12; p++)
                {
                    if(deSub[p] != 0 &&
                       deSub[p] != 255)
                        pOk = false;

                    for(int w = 0; w < 8; w++)
                        if(((deSub[p] >> w) & 1) > 0)
                            pWeight++;
                }

                for(int rw = subPos + 24; rw < subPos + 96; rw++)
                {
                    if(deSub[rw] == 0)
                        continue;

                    rwOk = false;

                    break;
                }

                bool rwPacket     = false;
                bool cdtextPacket = false;

                if(!rwOk)
                {
                    byte[] sectorSub = new byte[96];
                    Array.Copy(sub, subPos, sectorSub, 0, 96);

                    DetectRwPackets(sectorSub, out _, out rwPacket, out cdtextPacket);

                    // TODO: CD+G reed solomon
                    if(rwPacket && !cdtextPacket)
                        rwOk = true;

                    if(cdtextPacket)
                        rwOk = CheckCdTextPackets(sectorSub);
                }

                if(!pOk && _fixSubchannel)
                {
                    if(pWeight >= 48)
                        for(int p = subPos; p < subPos + 12; p++)
                            deSub[p] = 255;
                    else
                        for(int p = subPos; p < subPos + 12; p++)
                            deSub[p] = 0;

                    pOk    = true;
                    @fixed = true;

                    subLog.WritePFix(lba);
                }

                if(!rwOk         &&
                   !rwPacket     &&
                   !cdtextPacket &&
                   _fixSubchannel)
                {
                    for(int rw = subPos + 24; rw < subPos + 96; rw++)
                        deSub[rw] = 0;

                    rwOk   = true;
                    @fixed = true;

                    subLog.WriteRwFix(lba);
                }

                byte smin, ssec, amin, asec, aframe;
                int  aPos;

                if(!crcOk         &&
                   _fixSubchannel &&
                   subPos > 0     &&
                   subPos < deSub.Length - 96)
                {
                    isrcs.TryGetValue(currentTrack, out string knownGoodIsrc);

                    crcOk = FixQSubchannel(deSub, q, subPos, mcn, knownGoodIsrc, _fixSubchannelCrc, out bool fixedAdr,
                                           out bool controlFix, out bool fixedZero, out bool fixedTno,
                                           out bool fixedIndex, out bool fixedRelPos, out bool fixedAbsPos,
                                           out bool fixedCrc, out bool fixedMcn, out bool fixedIsrc);

                    if(crcOk)
                    {
                        Array.Copy(q, 0, deSub, subPos + 12, 12);
                        @fixed = true;

                        if(fixedAdr)
                            subLog.WriteQAdrFix(lba);

                        if(controlFix)
                            subLog.WriteQCtrlFix(lba);

                        if(fixedZero)
                            subLog.WriteQZeroFix(lba);

                        if(fixedTno)
                            subLog.WriteQTnoFix(lba);

                        if(fixedIndex)
                            subLog.WriteQIndexFix(lba);

                        if(fixedRelPos)
                            subLog.WriteQRelPosFix(lba);

                        if(fixedAbsPos)
                            subLog.WriteQAbsPosFix(lba);

                        if(fixedCrc)
                            subLog.WriteQCrcFix(lba);

                        if(fixedMcn)
                            subLog.WriteQMcnFix(lba);

                        if(fixedIsrc)
                            subLog.WriteQIsrcFix(lba);
                    }
                }

                if(!pOk   ||
                   !crcOk ||
                   !rwOk)
                    continue;

                aframe = (byte)(((q[9] / 16) * 10) + (q[9] & 0x0F));

                if((q[0] & 0x3) == 1)
                {
                    amin = (byte)(((q[7] / 16) * 10)       + (q[7] & 0x0F));
                    asec = (byte)(((q[8] / 16) * 10)       + (q[8] & 0x0F));
                    aPos = ((amin * 60 * 75) + (asec * 75) + aframe) - 150;
                }
                else
                {
                    ulong expectedSectorAddress = sectorAddress + (ulong)(subPos / 96) + 150;
                    smin                  =  (byte)(expectedSectorAddress / 60 / 75);
                    expectedSectorAddress -= (ulong)(smin * 60                 * 75);
                    ssec                  =  (byte)(expectedSectorAddress      / 75);

                    aPos = ((smin * 60 * 75) + (ssec * 75) + aframe) - 150;

                    // Next second
                    if(aPos < prePos)
                        aPos += 75;
                }

                // TODO: Negative sectors
                if(aPos < 0)
                    continue;

                prePos = aPos;

                byte[] posSub = new byte[96];
                Array.Copy(deSub, subPos, posSub, 0, 96);
                posSub = Subchannel.Interleave(posSub);
                _outputPlugin.WriteSectorTag(posSub, (ulong)aPos, SectorTagType.CdSectorSubchannel);

                subchannelExtents.Remove(aPos);

                if(@fixed)
                    subLog?.WriteEntry(posSub, supportedSubchannel == MmcSubchannel.Raw, lba, 1);
            }

            return indexesChanged;
        }

        bool CheckIndexesFromSubchannel(byte[] deSub, Dictionary<byte, string> isrcs, byte currentTrack, ref string mcn,
                                        Track[] tracks)
        {
            // Check subchannel
            for(int subPos = 0; subPos < deSub.Length; subPos += 96)
            {
                byte[] q = new byte[12];
                Array.Copy(deSub, subPos + 12, q, 0, 12);

                CRC16CCITTContext.Data(q, 10, out byte[] crc);
                bool crcOk = crc[0] == q[10] && crc[1] == q[11];

                // ISRC
                if((q[0] & 0x3) == 3)
                {
                    string isrc = Subchannel.DecodeIsrc(q);

                    if(isrc == null ||
                       isrc == "000000000000")
                        continue;

                    if(!crcOk)
                        continue;

                    if(!isrcs.ContainsKey(currentTrack))
                    {
                        _dumpLog?.WriteLine($"Found new ISRC {isrc} for track {currentTrack}.");
                        UpdateStatus?.Invoke($"Found new ISRC {isrc} for track {currentTrack}.");
                    }
                    else if(isrcs[currentTrack] != isrc)
                    {
                        _dumpLog?.
                            WriteLine($"ISRC for track {currentTrack} changed from {isrcs[currentTrack]} to {isrc}.");

                        UpdateStatus?.
                            Invoke($"ISRC for track {currentTrack} changed from {isrcs[currentTrack]} to {isrc}.");
                    }

                    isrcs[currentTrack] = isrc;
                }
                else if((q[0] & 0x3) == 2)
                {
                    string newMcn = Subchannel.DecodeMcn(q);

                    if(newMcn == null ||
                       newMcn == "0000000000000")
                        continue;

                    if(!crcOk)
                        continue;

                    if(mcn is null)
                    {
                        _dumpLog?.WriteLine($"Found new MCN {newMcn}.");
                        UpdateStatus?.Invoke($"Found new MCN {newMcn}.");
                    }
                    else if(mcn != newMcn)
                    {
                        _dumpLog?.WriteLine($"MCN changed from {mcn} to {newMcn}.");
                        UpdateStatus?.Invoke($"MCN changed from {mcn} to {newMcn}.");
                    }

                    mcn = newMcn;
                }
                else if((q[0] & 0x3) == 1)
                {
                    // TODO: Indexes

                    // Pregap
                    if(q[2] != 0)
                        continue;

                    if(!crcOk)
                        continue;

                    byte trackNo = (byte)(((q[1] / 16) * 10) + (q[1] & 0x0F));

                    for(int i = 0; i < tracks.Length; i++)
                    {
                        if(tracks[i].TrackSequence != trackNo ||
                           trackNo                 == 1)
                        {
                            continue;
                        }

                        byte pmin   = (byte)(((q[3] / 16) * 10) + (q[3] & 0x0F));
                        byte psec   = (byte)(((q[4] / 16) * 10) + (q[4] & 0x0F));
                        byte pframe = (byte)(((q[5] / 16) * 10) + (q[5] & 0x0F));
                        int  qPos   = (pmin * 60 * 75) + (psec * 75) + pframe;

                        if(tracks[i].TrackPregap >= (ulong)(qPos + 1))
                            continue;

                        tracks[i].TrackPregap      =  (ulong)(qPos + 1);
                        tracks[i].TrackStartSector -= tracks[i].TrackPregap;

                        if(i > 0)
                            tracks[i - 1].TrackEndSector = tracks[i].TrackStartSector - 1;

                        _dumpLog?.WriteLine($"Pregap for track {trackNo} set to {tracks[i].TrackPregap} sectors.");
                        UpdateStatus?.Invoke($"Pregap for track {trackNo} set to {tracks[i].TrackPregap} sectors.");

                        return true;
                    }
                }
            }

            return false;
        }

        void DetectRwPackets(byte[] subchannel, out bool zero, out bool rwPacket, out bool cdtextPacket)
        {
            zero         = false;
            rwPacket     = false;
            cdtextPacket = false;

            byte[] cdTextPack1  = new byte[18];
            byte[] cdTextPack2  = new byte[18];
            byte[] cdTextPack3  = new byte[18];
            byte[] cdTextPack4  = new byte[18];
            byte[] cdSubRwPack1 = new byte[24];
            byte[] cdSubRwPack2 = new byte[24];
            byte[] cdSubRwPack3 = new byte[24];
            byte[] cdSubRwPack4 = new byte[24];

            int i = 0;

            for(int j = 0; j < 18; j++)
            {
                cdTextPack1[j] = (byte)(cdTextPack1[j] | ((subchannel[i++] & 0x3F) << 2));

                cdTextPack1[j] = (byte)(cdTextPack1[j++] | ((subchannel[i] & 0xC0) >> 4));

                if(j < 18)
                    cdTextPack1[j] = (byte)(cdTextPack1[j] | ((subchannel[i++] & 0x0F) << 4));

                if(j < 18)
                    cdTextPack1[j] = (byte)(cdTextPack1[j++] | ((subchannel[i] & 0x3C) >> 2));

                if(j < 18)
                    cdTextPack1[j] = (byte)(cdTextPack1[j] | ((subchannel[i++] & 0x03) << 6));

                if(j < 18)
                    cdTextPack1[j] = (byte)(cdTextPack1[j] | (subchannel[i++] & 0x3F));
            }

            for(int j = 0; j < 18; j++)
            {
                cdTextPack2[j] = (byte)(cdTextPack2[j] | ((subchannel[i++] & 0x3F) << 2));

                cdTextPack2[j] = (byte)(cdTextPack2[j++] | ((subchannel[i] & 0xC0) >> 4));

                if(j < 18)
                    cdTextPack2[j] = (byte)(cdTextPack2[j] | ((subchannel[i++] & 0x0F) << 4));

                if(j < 18)
                    cdTextPack2[j] = (byte)(cdTextPack2[j++] | ((subchannel[i] & 0x3C) >> 2));

                if(j < 18)
                    cdTextPack2[j] = (byte)(cdTextPack2[j] | ((subchannel[i++] & 0x03) << 6));

                if(j < 18)
                    cdTextPack2[j] = (byte)(cdTextPack2[j] | (subchannel[i++] & 0x3F));
            }

            for(int j = 0; j < 18; j++)
            {
                cdTextPack3[j] = (byte)(cdTextPack3[j] | ((subchannel[i++] & 0x3F) << 2));

                cdTextPack3[j] = (byte)(cdTextPack3[j++] | ((subchannel[i] & 0xC0) >> 4));

                if(j < 18)
                    cdTextPack3[j] = (byte)(cdTextPack3[j] | ((subchannel[i++] & 0x0F) << 4));

                if(j < 18)
                    cdTextPack3[j] = (byte)(cdTextPack3[j++] | ((subchannel[i] & 0x3C) >> 2));

                if(j < 18)
                    cdTextPack3[j] = (byte)(cdTextPack3[j] | ((subchannel[i++] & 0x03) << 6));

                if(j < 18)
                    cdTextPack3[j] = (byte)(cdTextPack3[j] | (subchannel[i++] & 0x3F));
            }

            for(int j = 0; j < 18; j++)
            {
                cdTextPack4[j] = (byte)(cdTextPack4[j] | ((subchannel[i++] & 0x3F) << 2));

                cdTextPack4[j] = (byte)(cdTextPack4[j++] | ((subchannel[i] & 0xC0) >> 4));

                if(j < 18)
                    cdTextPack4[j] = (byte)(cdTextPack4[j] | ((subchannel[i++] & 0x0F) << 4));

                if(j < 18)
                    cdTextPack4[j] = (byte)(cdTextPack4[j++] | ((subchannel[i] & 0x3C) >> 2));

                if(j < 18)
                    cdTextPack4[j] = (byte)(cdTextPack4[j] | ((subchannel[i++] & 0x03) << 6));

                if(j < 18)
                    cdTextPack4[j] = (byte)(cdTextPack4[j] | (subchannel[i++] & 0x3F));
            }

            i = 0;

            for(int j = 0; j < 24; j++)
                cdSubRwPack1[j] = (byte)(subchannel[i++] & 0x3F);

            for(int j = 0; j < 24; j++)
                cdSubRwPack2[j] = (byte)(subchannel[i++] & 0x3F);

            for(int j = 0; j < 24; j++)
                cdSubRwPack3[j] = (byte)(subchannel[i++] & 0x3F);

            for(int j = 0; j < 24; j++)
                cdSubRwPack4[j] = (byte)(subchannel[i++] & 0x3F);

            switch(cdSubRwPack1[0])
            {
                case 0x00:
                    zero = true;

                    break;
                case 0x08:
                case 0x09:
                case 0x0A:
                case 0x18:
                case 0x38:
                    rwPacket = true;

                    break;
                case 0x14:
                    cdtextPacket = true;

                    break;
            }

            switch(cdSubRwPack2[0])
            {
                case 0x00:
                    zero = true;

                    break;
                case 0x08:
                case 0x09:
                case 0x0A:
                case 0x18:
                case 0x38:
                    rwPacket = true;

                    break;
                case 0x14:
                    cdtextPacket = true;

                    break;
            }

            switch(cdSubRwPack3[0])
            {
                case 0x00:
                    zero = true;

                    break;
                case 0x08:
                case 0x09:
                case 0x0A:
                case 0x18:
                case 0x38:
                    rwPacket = true;

                    break;
                case 0x14:
                    cdtextPacket = true;

                    break;
            }

            switch(cdSubRwPack4[0])
            {
                case 0x00:
                    zero = true;

                    break;
                case 0x08:
                case 0x09:
                case 0x0A:
                case 0x18:
                case 0x38:
                    rwPacket = true;

                    break;
                case 0x14:
                    cdtextPacket = true;

                    break;
            }

            if((cdTextPack1[0] & 0x80) == 0x80)
                cdtextPacket = true;

            if((cdTextPack2[0] & 0x80) == 0x80)
                cdtextPacket = true;

            if((cdTextPack3[0] & 0x80) == 0x80)
                cdtextPacket = true;

            if((cdTextPack4[0] & 0x80) == 0x80)
                cdtextPacket = true;
        }

        bool CheckCdTextPackets(byte[] subchannel)
        {
            byte[] cdTextPack1 = new byte[18];
            byte[] cdTextPack2 = new byte[18];
            byte[] cdTextPack3 = new byte[18];
            byte[] cdTextPack4 = new byte[18];

            int i = 0;

            for(int j = 0; j < 18; j++)
            {
                cdTextPack1[j] = (byte)(cdTextPack1[j] | ((subchannel[i++] & 0x3F) << 2));

                cdTextPack1[j] = (byte)(cdTextPack1[j++] | ((subchannel[i] & 0xC0) >> 4));

                if(j < 18)
                    cdTextPack1[j] = (byte)(cdTextPack1[j] | ((subchannel[i++] & 0x0F) << 4));

                if(j < 18)
                    cdTextPack1[j] = (byte)(cdTextPack1[j++] | ((subchannel[i] & 0x3C) >> 2));

                if(j < 18)
                    cdTextPack1[j] = (byte)(cdTextPack1[j] | ((subchannel[i++] & 0x03) << 6));

                if(j < 18)
                    cdTextPack1[j] = (byte)(cdTextPack1[j] | (subchannel[i++] & 0x3F));
            }

            for(int j = 0; j < 18; j++)
            {
                cdTextPack2[j] = (byte)(cdTextPack2[j] | ((subchannel[i++] & 0x3F) << 2));

                cdTextPack2[j] = (byte)(cdTextPack2[j++] | ((subchannel[i] & 0xC0) >> 4));

                if(j < 18)
                    cdTextPack2[j] = (byte)(cdTextPack2[j] | ((subchannel[i++] & 0x0F) << 4));

                if(j < 18)
                    cdTextPack2[j] = (byte)(cdTextPack2[j++] | ((subchannel[i] & 0x3C) >> 2));

                if(j < 18)
                    cdTextPack2[j] = (byte)(cdTextPack2[j] | ((subchannel[i++] & 0x03) << 6));

                if(j < 18)
                    cdTextPack2[j] = (byte)(cdTextPack2[j] | (subchannel[i++] & 0x3F));
            }

            for(int j = 0; j < 18; j++)
            {
                cdTextPack3[j] = (byte)(cdTextPack3[j] | ((subchannel[i++] & 0x3F) << 2));

                cdTextPack3[j] = (byte)(cdTextPack3[j++] | ((subchannel[i] & 0xC0) >> 4));

                if(j < 18)
                    cdTextPack3[j] = (byte)(cdTextPack3[j] | ((subchannel[i++] & 0x0F) << 4));

                if(j < 18)
                    cdTextPack3[j] = (byte)(cdTextPack3[j++] | ((subchannel[i] & 0x3C) >> 2));

                if(j < 18)
                    cdTextPack3[j] = (byte)(cdTextPack3[j] | ((subchannel[i++] & 0x03) << 6));

                if(j < 18)
                    cdTextPack3[j] = (byte)(cdTextPack3[j] | (subchannel[i++] & 0x3F));
            }

            for(int j = 0; j < 18; j++)
            {
                cdTextPack4[j] = (byte)(cdTextPack4[j] | ((subchannel[i++] & 0x3F) << 2));

                cdTextPack4[j] = (byte)(cdTextPack4[j++] | ((subchannel[i] & 0xC0) >> 4));

                if(j < 18)
                    cdTextPack4[j] = (byte)(cdTextPack4[j] | ((subchannel[i++] & 0x0F) << 4));

                if(j < 18)
                    cdTextPack4[j] = (byte)(cdTextPack4[j++] | ((subchannel[i] & 0x3C) >> 2));

                if(j < 18)
                    cdTextPack4[j] = (byte)(cdTextPack4[j] | ((subchannel[i++] & 0x03) << 6));

                if(j < 18)
                    cdTextPack4[j] = (byte)(cdTextPack4[j] | (subchannel[i++] & 0x3F));
            }

            bool status = true;

            if((cdTextPack1[0] & 0x80) == 0x80)
            {
                ushort cdTextPack1Crc    = BigEndianBitConverter.ToUInt16(cdTextPack1, 16);
                byte[] cdTextPack1ForCrc = new byte[16];
                Array.Copy(cdTextPack1, 0, cdTextPack1ForCrc, 0, 16);
                ushort calculatedCdtp1Crc = CRC16CCITTContext.Calculate(cdTextPack1ForCrc);

                if(cdTextPack1Crc != calculatedCdtp1Crc &&
                   cdTextPack1Crc != 0)
                    status = false;
            }

            if((cdTextPack2[0] & 0x80) == 0x80)
            {
                ushort cdTextPack2Crc    = BigEndianBitConverter.ToUInt16(cdTextPack2, 16);
                byte[] cdTextPack2ForCrc = new byte[16];
                Array.Copy(cdTextPack2, 0, cdTextPack2ForCrc, 0, 16);
                ushort calculatedCdtp2Crc = CRC16CCITTContext.Calculate(cdTextPack2ForCrc);

                if(cdTextPack2Crc != calculatedCdtp2Crc &&
                   cdTextPack2Crc != 0)
                    status = false;
            }

            if((cdTextPack3[0] & 0x80) == 0x80)
            {
                ushort cdTextPack3Crc    = BigEndianBitConverter.ToUInt16(cdTextPack3, 16);
                byte[] cdTextPack3ForCrc = new byte[16];
                Array.Copy(cdTextPack3, 0, cdTextPack3ForCrc, 0, 16);
                ushort calculatedCdtp3Crc = CRC16CCITTContext.Calculate(cdTextPack3ForCrc);

                if(cdTextPack3Crc != calculatedCdtp3Crc &&
                   cdTextPack3Crc != 0)
                    status = false;
            }

            if((cdTextPack4[0] & 0x80) != 0x80)
                return status;

            ushort cdTextPack4Crc    = BigEndianBitConverter.ToUInt16(cdTextPack4, 16);
            byte[] cdTextPack4ForCrc = new byte[16];
            Array.Copy(cdTextPack4, 0, cdTextPack4ForCrc, 0, 16);
            ushort calculatedCdtp4Crc = CRC16CCITTContext.Calculate(cdTextPack4ForCrc);

            if(cdTextPack4Crc == calculatedCdtp4Crc ||
               cdTextPack4Crc == 0)
                return status;

            return false;
        }

        bool FixQSubchannel(byte[] deSub, byte[] q, int subPos, string mcn, string isrc, bool fixCrc, out bool fixedAdr,
                            out bool controlFix, out bool fixedZero, out bool fixedTno, out bool fixedIndex,
                            out bool fixedRelPos, out bool fixedAbsPos, out bool fixedCrc, out bool fixedMcn,
                            out bool fixedIsrc)
        {
            byte amin, asec, aframe, pmin, psec, pframe;
            byte rmin, rsec, rframe;
            int  aPos, rPos, pPos, dPos;
            controlFix  = false;
            fixedZero   = false;
            fixedTno    = false;
            fixedIndex  = false;
            fixedRelPos = false;
            fixedAbsPos = false;
            fixedCrc    = false;
            fixedMcn    = false;
            fixedIsrc   = false;

            byte[] preQ  = new byte[12];
            byte[] nextQ = new byte[12];
            Array.Copy(deSub, (subPos + 12) - 96, preQ, 0, 12);
            Array.Copy(deSub, subPos + 12   + 96, nextQ, 0, 12);
            bool status;

            CRC16CCITTContext.Data(preQ, 10, out byte[] preCrc);
            bool preCrcOk = preCrc[0] == preQ[10] && preCrc[1] == preQ[11];

            CRC16CCITTContext.Data(nextQ, 10, out byte[] nextCrc);
            bool nextCrcOk = nextCrc[0] == nextQ[10] && nextCrc[1] == nextQ[11];

            fixedAdr = false;

            // Extraneous bits in ADR
            if((q[0] & 0xC) != 0)
            {
                q[0]     &= 0xF3;
                fixedAdr =  true;
            }

            CRC16CCITTContext.Data(q, 10, out byte[] qCrc);
            status = qCrc[0] == q[10] && qCrc[1] == q[11];

            if(fixedAdr && status)
                return true;

            int oldAdr = q[0] & 0x3;

            // Try Q-Mode 1
            q[0] = (byte)((q[0] & 0xF0) + 1);
            CRC16CCITTContext.Data(q, 10, out qCrc);
            status = qCrc[0] == q[10] && qCrc[1] == q[11];

            if(status)
            {
                fixedAdr = true;

                return true;
            }

            // Try Q-Mode 2
            q[0] = (byte)((q[0] & 0xF0) + 2);
            CRC16CCITTContext.Data(q, 10, out qCrc);
            status = qCrc[0] == q[10] && qCrc[1] == q[11];

            if(status)
            {
                fixedAdr = true;

                return true;
            }

            // Try Q-Mode 3
            q[0] = (byte)((q[0] & 0xF0) + 3);
            CRC16CCITTContext.Data(q, 10, out qCrc);
            status = qCrc[0] == q[10] && qCrc[1] == q[11];

            if(status)
            {
                fixedAdr = true;

                return true;
            }

            q[0] = (byte)((q[0] & 0xF0) + oldAdr);

            oldAdr = q[0];

            // Try using previous control
            if(preCrcOk && (q[0] & 0xF0) != (preQ[0] & 0xF0))
            {
                q[0] = (byte)((q[0] & 0x03) + (preQ[0] & 0xF0));

                CRC16CCITTContext.Data(q, 10, out qCrc);
                status = qCrc[0] == q[10] && qCrc[1] == q[11];

                if(status)
                {
                    controlFix = true;

                    return true;
                }

                q[0] = (byte)oldAdr;
            }

            // Try using next control
            if(nextCrcOk && (q[0] & 0xF0) != (nextQ[0] & 0xF0))
            {
                q[0] = (byte)((q[0] & 0x03) + (nextQ[0] & 0xF0));

                CRC16CCITTContext.Data(q, 10, out qCrc);
                status = qCrc[0] == q[10] && qCrc[1] == q[11];

                if(status)
                {
                    controlFix = true;

                    return true;
                }

                q[0] = (byte)oldAdr;
            }

            if(preCrcOk                               &&
               nextCrcOk                              &&
               (nextQ[0] & 0xF0) == (preQ[0]  & 0xF0) &&
               (q[0]     & 0xF0) != (nextQ[0] & 0xF0))
            {
                q[0] = (byte)((q[0] & 0x03) + (nextQ[0] & 0xF0));

                controlFix = true;
            }

            if((q[0] & 0x3) == 1)
            {
                // ZERO not zero
                if(q[6] != 0)
                {
                    q[6]      = 0;
                    fixedZero = true;

                    CRC16CCITTContext.Data(q, 10, out qCrc);
                    status = qCrc[0] == q[10] && qCrc[1] == q[11];

                    if(status)
                        return true;
                }

                if(preCrcOk && nextCrcOk)
                {
                    if(preQ[1] == nextQ[1] &&
                       preQ[1] != q[1])
                    {
                        q[1]     = preQ[1];
                        fixedTno = true;

                        CRC16CCITTContext.Data(q, 10, out qCrc);
                        status = qCrc[0] == q[10] && qCrc[1] == q[11];

                        if(status)
                            return true;
                    }
                }

                if(preCrcOk && nextCrcOk)
                {
                    if(preQ[2] == nextQ[2] &&
                       preQ[2] != q[2])
                    {
                        q[2]       = preQ[2];
                        fixedIndex = true;

                        CRC16CCITTContext.Data(q, 10, out qCrc);
                        status = qCrc[0] == q[10] && qCrc[1] == q[11];

                        if(status)
                            return true;
                    }
                }

                amin   = (byte)(((q[7] / 16) * 10)       + (q[7] & 0x0F));
                asec   = (byte)(((q[8] / 16) * 10)       + (q[8] & 0x0F));
                aframe = (byte)(((q[9] / 16) * 10)       + (q[9] & 0x0F));
                aPos   = ((amin * 60 * 75) + (asec * 75) + aframe) - 150;

                pmin   = (byte)(((q[3] / 16) * 10) + (q[3] & 0x0F));
                psec   = (byte)(((q[4] / 16) * 10) + (q[4] & 0x0F));
                pframe = (byte)(((q[5] / 16) * 10) + (q[5] & 0x0F));
                pPos   = (pmin * 60 * 75) + (psec * 75) + pframe;

                // TODO: pregap
                // Not pregap
                if(q[2] > 0)
                {
                    // Previous was not pregap either
                    if(preQ[2] > 0 && preCrcOk)
                    {
                        rmin   = (byte)(((preQ[3] / 16) * 10) + (preQ[3] & 0x0F));
                        rsec   = (byte)(((preQ[4] / 16) * 10) + (preQ[4] & 0x0F));
                        rframe = (byte)(((preQ[5] / 16) * 10) + (preQ[5] & 0x0F));
                        rPos   = (rmin * 60 * 75) + (rsec * 75) + rframe;

                        dPos = pPos - rPos;

                        if(dPos != 1)
                        {
                            q[3] = preQ[3];
                            q[4] = preQ[4];
                            q[5] = preQ[5];

                            // BCD add 1, so 0x39 becomes 0x40
                            if((q[5] & 0xF) == 9)
                                q[5] += 7;
                            else
                                q[5]++;

                            // 74 frames, so from 0x00 to 0x74, BCD
                            if(q[5] >= 0x74)
                            {
                                // 0 frames
                                q[5] = 0;

                                // Add 1 second
                                if((q[4] & 0xF) == 9)
                                    q[4] += 7;
                                else
                                    q[4]++;

                                // 60 seconds, so from 0x00 to 0x59, BCD
                                if(q[4] >= 0x59)
                                {
                                    // 0 seconds
                                    q[4] = 0;

                                    // Add 1 minute
                                    q[3]++;
                                }
                            }

                            fixedRelPos = true;

                            CRC16CCITTContext.Data(q, 10, out qCrc);
                            status = qCrc[0] == q[10] && qCrc[1] == q[11];

                            if(status)
                                return true;
                        }
                    }

                    // Next is not pregap and we didn't fix relative position with previous
                    if(nextQ[2] > 0 &&
                       nextCrcOk    &&
                       !fixedRelPos)
                    {
                        rmin   = (byte)(((nextQ[3] / 16) * 10) + (nextQ[3] & 0x0F));
                        rsec   = (byte)(((nextQ[4] / 16) * 10) + (nextQ[4] & 0x0F));
                        rframe = (byte)(((nextQ[5] / 16) * 10) + (nextQ[5] & 0x0F));
                        rPos   = (rmin * 60 * 75) + (rsec * 75) + rframe;

                        dPos = rPos - pPos;

                        if(dPos != 1)
                        {
                            q[3] = nextQ[3];
                            q[4] = nextQ[4];
                            q[5] = nextQ[5];

                            // If frames is 0
                            if(q[5] == 0)
                            {
                                // If seconds is 0
                                if(q[4] == 0)
                                {
                                    // BCD decrease minutes
                                    if((q[3] & 0xF) == 0)
                                        q[3] = (byte)((q[3] & 0xF0) - 0x10);
                                    else
                                        q[3]--;

                                    q[4] = 0x59;
                                    q[5] = 0x73;
                                }
                                else
                                {
                                    // BCD decrease seconds
                                    if((q[4] & 0xF) == 0)
                                        q[4] = (byte)((q[4] & 0xF0) - 0x10);
                                    else
                                        q[4]--;

                                    q[5] = 0x73;
                                }
                            }

                            // BCD decrease frames
                            else if((q[5] & 0xF) == 0)
                                q[5] = (byte)((q[5] & 0xF0) - 0x10);
                            else
                                q[5]--;

                            fixedRelPos = true;

                            CRC16CCITTContext.Data(q, 10, out qCrc);
                            status = qCrc[0] == q[10] && qCrc[1] == q[11];

                            if(status)
                                return true;
                        }
                    }
                }

                if(preCrcOk)
                {
                    rmin   = (byte)(((preQ[7] / 16) * 10)    + (preQ[7] & 0x0F));
                    rsec   = (byte)(((preQ[8] / 16) * 10)    + (preQ[8] & 0x0F));
                    rframe = (byte)(((preQ[9] / 16) * 10)    + (preQ[9] & 0x0F));
                    rPos   = ((rmin * 60 * 75) + (rsec * 75) + rframe) - 150;

                    dPos = aPos - rPos;

                    if(dPos != 1)
                    {
                        q[7] = preQ[7];
                        q[8] = preQ[8];
                        q[9] = preQ[9];

                        // BCD add 1, so 0x39 becomes 0x40
                        if((q[9] & 0xF) == 9)
                            q[9] += 7;
                        else
                            q[9]++;

                        // 74 frames, so from 0x00 to 0x74, BCD
                        if(q[9] >= 0x74)
                        {
                            // 0 frames
                            q[9] = 0;

                            // Add 1 second
                            if((q[8] & 0xF) == 9)
                                q[8] += 7;
                            else
                                q[8]++;

                            // 60 seconds, so from 0x00 to 0x59, BCD
                            if(q[8] >= 0x59)
                            {
                                // 0 seconds
                                q[8] = 0;

                                // Add 1 minute
                                q[7]++;
                            }
                        }

                        fixedAbsPos = true;

                        CRC16CCITTContext.Data(q, 10, out qCrc);
                        status = qCrc[0] == q[10] && qCrc[1] == q[11];

                        if(status)
                            return true;
                    }
                }

                // Next is not pregap and we didn't fix relative position with previous
                if(nextQ[2] > 0 &&
                   nextCrcOk    &&
                   !fixedAbsPos)
                {
                    rmin   = (byte)(((nextQ[7] / 16) * 10)   + (nextQ[7] & 0x0F));
                    rsec   = (byte)(((nextQ[8] / 16) * 10)   + (nextQ[8] & 0x0F));
                    rframe = (byte)(((nextQ[9] / 16) * 10)   + (nextQ[9] & 0x0F));
                    rPos   = ((rmin * 60 * 75) + (rsec * 75) + rframe) - 150;

                    dPos = rPos - pPos;

                    if(dPos != 1)
                    {
                        q[7] = nextQ[7];
                        q[8] = nextQ[8];
                        q[9] = nextQ[9];

                        // If frames is 0
                        if(q[9] == 0)
                        {
                            // If seconds is 0
                            if(q[8] == 0)
                            {
                                // BCD decrease minutes
                                if((q[7] & 0xF) == 0)
                                    q[7] = (byte)((q[7] & 0xF0) - 0x10);
                                else
                                    q[7]--;

                                q[8] = 0x59;
                                q[9] = 0x73;
                            }
                            else
                            {
                                // BCD decrease seconds
                                if((q[8] & 0xF) == 0)
                                    q[8] = (byte)((q[8] & 0xF0) - 0x10);
                                else
                                    q[8]--;

                                q[9] = 0x73;
                            }
                        }

                        // BCD decrease frames
                        else if((q[9] & 0xF) == 0)
                            q[9] = (byte)((q[9] & 0xF0) - 0x10);
                        else
                            q[9]--;

                        fixedAbsPos = true;

                        CRC16CCITTContext.Data(q, 10, out qCrc);
                        status = qCrc[0] == q[10] && qCrc[1] == q[11];

                        if(status)
                            return true;
                    }
                }

                CRC16CCITTContext.Data(q, 10, out qCrc);
                status = qCrc[0] == q[10] && qCrc[1] == q[11];

                // Game Over
                if(!fixCrc || status)
                    return false;

                if(preCrcOk)
                {
                    rmin   = (byte)(((preQ[7] / 16) * 10)    + (preQ[7] & 0x0F));
                    rsec   = (byte)(((preQ[8] / 16) * 10)    + (preQ[8] & 0x0F));
                    rframe = (byte)(((preQ[9] / 16) * 10)    + (preQ[9] & 0x0F));
                    rPos   = ((rmin * 60 * 75) + (rsec * 75) + rframe) - 150;

                    dPos = aPos - rPos;

                    bool absOk = dPos == 1;

                    rmin   = (byte)(((preQ[3] / 16) * 10) + (preQ[3] & 0x0F));
                    rsec   = (byte)(((preQ[4] / 16) * 10) + (preQ[4] & 0x0F));
                    rframe = (byte)(((preQ[5] / 16) * 10) + (preQ[5] & 0x0F));
                    rPos   = (rmin * 60 * 75) + (rsec * 75) + rframe;

                    dPos = pPos - rPos;

                    bool relOk = dPos == 1;

                    if(q[0] != preQ[0] ||
                       q[1] != preQ[1] ||
                       q[2] != preQ[2] ||
                       q[6] != 0       ||
                       !absOk          ||
                       !relOk)
                        return false;

                    CRC16CCITTContext.Data(q, 10, out qCrc);
                    q[10] = qCrc[0];
                    q[11] = qCrc[1];

                    fixedCrc = true;

                    return true;
                }

                if(nextCrcOk)
                {
                    rmin   = (byte)(((nextQ[7] / 16) * 10)   + (nextQ[7] & 0x0F));
                    rsec   = (byte)(((nextQ[8] / 16) * 10)   + (nextQ[8] & 0x0F));
                    rframe = (byte)(((nextQ[9] / 16) * 10)   + (nextQ[9] & 0x0F));
                    rPos   = ((rmin * 60 * 75) + (rsec * 75) + rframe) - 150;

                    dPos = rPos - aPos;

                    bool absOk = dPos == 1;

                    rmin   = (byte)(((nextQ[3] / 16) * 10) + (nextQ[3] & 0x0F));
                    rsec   = (byte)(((nextQ[4] / 16) * 10) + (nextQ[4] & 0x0F));
                    rframe = (byte)(((nextQ[5] / 16) * 10) + (nextQ[5] & 0x0F));
                    rPos   = (rmin * 60 * 75) + (rsec * 75) + rframe;

                    dPos = rPos - pPos;

                    bool relOk = dPos == 1;

                    if(q[0] != nextQ[0] ||
                       q[1] != nextQ[1] ||
                       q[2] != nextQ[2] ||
                       q[6] != 0        ||
                       !absOk           ||
                       !relOk)
                        return false;

                    CRC16CCITTContext.Data(q, 10, out qCrc);
                    q[10] = qCrc[0];
                    q[11] = qCrc[1];

                    fixedCrc = true;

                    return true;
                }

                // Ok if previous and next are both BAD I won't rewrite the CRC at all
            }
            else if((q[0] & 0x3) == 2)
            {
                if(preCrcOk)
                {
                    rframe = (byte)(((preQ[9] / 16) * 10) + (preQ[9] & 0x0F));
                    aframe = (byte)(((q[9]    / 16) * 10) + (q[9]    & 0x0F));

                    if(aframe - rframe != 1)
                    {
                        q[9] = preQ[9];

                        if((q[9] & 0xF) == 9)
                            q[9] += 7;
                        else
                            q[9]++;

                        if(q[9] >= 0x74)
                            q[9] = 0;

                        fixedAbsPos = true;

                        CRC16CCITTContext.Data(q, 10, out qCrc);
                        status = qCrc[0] == q[10] && qCrc[1] == q[11];

                        if(status)
                            return true;
                    }
                }
                else if(nextCrcOk)
                {
                    rframe = (byte)(((nextQ[9] / 16) * 10) + (nextQ[9] & 0x0F));
                    aframe = (byte)(((q[9]     / 16) * 10) + (q[9]     & 0x0F));

                    if(aframe - rframe != 1)
                    {
                        q[9] = nextQ[9];

                        if(q[9] == 0)
                            q[9] = 0x73;
                        else if((q[9] & 0xF) == 0)
                            q[9] = (byte)((q[9] & 0xF0) - 0x10);
                        else
                            q[9]--;

                        fixedAbsPos = true;

                        CRC16CCITTContext.Data(q, 10, out qCrc);
                        status = qCrc[0] == q[10] && qCrc[1] == q[11];

                        if(status)
                            return true;
                    }
                }

                if(mcn != null)
                {
                    q[1] = (byte)((((mcn[0]  - 0x30) & 0x0F) * 16) + ((mcn[1]  - 0x30) & 0x0F));
                    q[2] = (byte)((((mcn[2]  - 0x30) & 0x0F) * 16) + ((mcn[3]  - 0x30) & 0x0F));
                    q[3] = (byte)((((mcn[4]  - 0x30) & 0x0F) * 16) + ((mcn[5]  - 0x30) & 0x0F));
                    q[4] = (byte)((((mcn[6]  - 0x30) & 0x0F) * 16) + ((mcn[7]  - 0x30) & 0x0F));
                    q[5] = (byte)((((mcn[8]  - 0x30) & 0x0F) * 16) + ((mcn[9]  - 0x30) & 0x0F));
                    q[6] = (byte)((((mcn[10] - 0x30) & 0x0F) * 16) + ((mcn[11] - 0x30) & 0x0F));
                    q[7] = (byte)(((mcn[12]                                    - 0x30) & 0x0F) * 8);
                    q[8] = 0;

                    fixedMcn = true;

                    CRC16CCITTContext.Data(q, 10, out qCrc);
                    status = qCrc[0] == q[10] && qCrc[1] == q[11];

                    if(status)
                        return true;
                }

                if(!fixCrc    ||
                   !nextCrcOk ||
                   !preCrcOk)
                    return false;

                CRC16CCITTContext.Data(q, 10, out qCrc);
                q[10] = qCrc[0];
                q[11] = qCrc[1];

                fixedCrc = true;

                return true;
            }
            else if((q[0] & 0x3) == 3)
            {
                if(preCrcOk)
                {
                    rframe = (byte)(((preQ[9] / 16) * 10) + (preQ[9] & 0x0F));
                    aframe = (byte)(((q[9]    / 16) * 10) + (q[9]    & 0x0F));

                    if(aframe - rframe != 1)
                    {
                        q[9] = preQ[9];

                        if((q[9] & 0xF) == 9)
                            q[9] += 7;
                        else
                            q[9]++;

                        if(q[9] >= 0x74)
                            q[9] = 0;

                        fixedAbsPos = true;

                        CRC16CCITTContext.Data(q, 10, out qCrc);
                        status = qCrc[0] == q[10] && qCrc[1] == q[11];

                        if(status)
                            return true;
                    }
                }
                else if(nextCrcOk)
                {
                    rframe = (byte)(((nextQ[9] / 16) * 10) + (nextQ[9] & 0x0F));
                    aframe = (byte)(((q[9]     / 16) * 10) + (q[9]     & 0x0F));

                    if(aframe - rframe != 1)
                    {
                        q[9] = nextQ[9];

                        if(q[9] == 0)
                            q[9] = 0x73;
                        else if((q[9] & 0xF) == 0)
                            q[9] = (byte)((q[9] & 0xF0) - 0x10);
                        else
                            q[9]--;

                        fixedAbsPos = true;

                        CRC16CCITTContext.Data(q, 10, out qCrc);
                        status = qCrc[0] == q[10] && qCrc[1] == q[11];

                        if(status)
                            return true;
                    }
                }

                if(isrc != null)
                {
                    byte i1 = Subchannel.GetIsrcCode(isrc[0]);
                    byte i2 = Subchannel.GetIsrcCode(isrc[1]);
                    byte i3 = Subchannel.GetIsrcCode(isrc[2]);
                    byte i4 = Subchannel.GetIsrcCode(isrc[3]);
                    byte i5 = Subchannel.GetIsrcCode(isrc[4]);

                    q[1] = (byte)((i1 << 2) + ((i2 & 0x30) >> 4));
                    q[2] = (byte)(((i2             & 0xF)  << 4) + (i3 >> 2));
                    q[3] = (byte)(((i3             & 0x3)  << 6) + i4);
                    q[4] = (byte)(i5 << 2);
                    q[5] = (byte)((((isrc[5] - 0x30) & 0x0F) * 16) + ((isrc[6]  - 0x30) & 0x0F));
                    q[6] = (byte)((((isrc[7] - 0x30) & 0x0F) * 16) + ((isrc[8]  - 0x30) & 0x0F));
                    q[7] = (byte)((((isrc[9] - 0x30) & 0x0F) * 16) + ((isrc[10] - 0x30) & 0x0F));
                    q[8] = (byte)(((isrc[11]                                    - 0x30) & 0x0F) * 16);

                    fixedIsrc = true;

                    CRC16CCITTContext.Data(q, 10, out qCrc);
                    status = qCrc[0] == q[10] && qCrc[1] == q[11];

                    if(status)
                        return true;
                }

                if(!fixCrc    ||
                   !nextCrcOk ||
                   !preCrcOk)
                    return false;

                CRC16CCITTContext.Data(q, 10, out qCrc);
                q[10] = qCrc[0];
                q[11] = qCrc[1];

                fixedCrc = true;

                return true;
            }

            return false;
        }
    }
}