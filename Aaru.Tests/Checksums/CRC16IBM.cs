﻿// /***************************************************************************
// Aaru Data Preservation Suite
// ----------------------------------------------------------------------------
//
// Filename       : CRC16IBM.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : Aaru unit testing.
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
// Copyright © 2011-2021 Natalia Portillo
// ****************************************************************************/

using System.IO;
using Aaru.Checksums;
using Aaru.CommonTypes.Interfaces;
using NUnit.Framework;

namespace Aaru.Tests.Checksums
{
    [TestFixture]
    public class Crc16Ibm
    {
        static readonly byte[] _expectedEmpty =
        {
            0x00, 0x00
        };
        static readonly byte[] _expectedRandom =
        {
            0x2d, 0x6d
        };

        [Test]
        public void Crc16IbmEmptyData()
        {
            byte[] data = new byte[1048576];

            var fs = new FileStream(Path.Combine(Consts.TEST_FILES_ROOT, "Checksum test files", "empty"), FileMode.Open,
                                    FileAccess.Read);

            fs.Read(data, 0, 1048576);
            fs.Close();
            fs.Dispose();
            CRC16IBMContext.Data(data, out byte[] result);
            Assert.AreEqual(_expectedEmpty, result);
        }

        [Test]
        public void Crc16IbmEmptyFile()
        {
            byte[] result = CRC16IBMContext.File(Path.Combine(Consts.TEST_FILES_ROOT, "Checksum test files", "empty"));

            Assert.AreEqual(_expectedEmpty, result);
        }

        [Test]
        public void Crc16IbmEmptyInstance()
        {
            byte[] data = new byte[1048576];

            var fs = new FileStream(Path.Combine(Consts.TEST_FILES_ROOT, "Checksum test files", "empty"), FileMode.Open,
                                    FileAccess.Read);

            fs.Read(data, 0, 1048576);
            fs.Close();
            fs.Dispose();
            IChecksum ctx = new CRC16IBMContext();
            ctx.Update(data);
            byte[] result = ctx.Final();
            Assert.AreEqual(_expectedEmpty, result);
        }

        [Test]
        public void Crc16IbmRandomData()
        {
            byte[] data = new byte[1048576];

            var fs = new FileStream(Path.Combine(Consts.TEST_FILES_ROOT, "Checksum test files", "random"),
                                    FileMode.Open, FileAccess.Read);

            fs.Read(data, 0, 1048576);
            fs.Close();
            fs.Dispose();
            CRC16IBMContext.Data(data, out byte[] result);
            Assert.AreEqual(_expectedRandom, result);
        }

        [Test]
        public void Crc16IbmRandomFile()
        {
            byte[] result = CRC16IBMContext.File(Path.Combine(Consts.TEST_FILES_ROOT, "Checksum test files", "random"));

            Assert.AreEqual(_expectedRandom, result);
        }

        [Test]
        public void Crc16IbmRandomInstance()
        {
            byte[] data = new byte[1048576];

            var fs = new FileStream(Path.Combine(Consts.TEST_FILES_ROOT, "Checksum test files", "random"),
                                    FileMode.Open, FileAccess.Read);

            fs.Read(data, 0, 1048576);
            fs.Close();
            fs.Dispose();
            IChecksum ctx = new CRC16IBMContext();
            ctx.Update(data);
            byte[] result = ctx.Final();
            Assert.AreEqual(_expectedRandom, result);
        }
    }
}