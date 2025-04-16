/* Copyright (C) 2018-2025 Tal Aloni <tal.aloni.il@gmail.com>. All rights reserved.
 * 
 * You can redistribute this program and/or modify it under the terms of
 * the GNU Lesser Public License as published by the Free Software Foundation,
 * either version 3 of the License, or (at your option) any later version.
 */
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Utilities;

namespace DiskAccessLibrary.Tests.UnitTests.Disks.VMDK
{
    [TestClass]
    public class VirtualMachineDiskTests
    {
        private const string MonolithicSparseVmdkPath = @"TestData\VMDK\MonolithicSparse.vmdk";
        private const string StreamOptimizedeVmdkPath = @"TestData\VMDK\StreamOptimized.vmdk";

        [TestMethod]
        public void MonolithicSparse_ReadSequential()
        {
            VirtualMachineDisk virtualMachineDisk = new VirtualMachineDisk(MonolithicSparseVmdkPath);
            TestDiskContent(virtualMachineDisk);
        }

        [TestMethod]
        public void StreamOptimized_ReadSequential()
        {
            VirtualMachineDisk virtualMachineDisk = new VirtualMachineDisk(StreamOptimizedeVmdkPath);
            TestDiskContent(virtualMachineDisk);
        }

        [TestMethod]
        public void MonolithicSparse_ReadSector_ReadFromGrainStart()
        {
            VirtualMachineDisk virtualMachineDisk = new VirtualMachineDisk(MonolithicSparseVmdkPath);
            byte[] buffer = virtualMachineDisk.ReadSector(0);
            Assert.AreEqual(VirtualMachineDisk.BytesPerDiskSector, buffer.Length);
        }

        [TestMethod]
        public void StreamOptimized_ReadSector_ReadFromGrainStart()
        {
            VirtualMachineDisk virtualMachineDisk = new VirtualMachineDisk(StreamOptimizedeVmdkPath);
            byte[] buffer = virtualMachineDisk.ReadSector(0);
            Assert.AreEqual(VirtualMachineDisk.BytesPerDiskSector, buffer.Length);
        }

        [TestMethod]
        public void MonolithicSparse_ReadSector_FromTheMiddleOfAGrain()
        {
            VirtualMachineDisk virtualMachineDisk = new VirtualMachineDisk(MonolithicSparseVmdkPath);
            byte[] buffer = virtualMachineDisk.ReadSector(1);
            Assert.AreEqual(VirtualMachineDisk.BytesPerDiskSector, buffer.Length);
            Assert.AreEqual(1, BigEndianConverter.ToInt64(buffer, 0));
        }

        [TestMethod]
        public void StreamOptimized_ReadSector_FromTheMiddleOfACompressedGrain()
        {
            VirtualMachineDisk virtualMachineDisk = new VirtualMachineDisk(StreamOptimizedeVmdkPath);
            byte[] buffer = virtualMachineDisk.ReadSector(1);
            Assert.AreEqual(VirtualMachineDisk.BytesPerDiskSector, buffer.Length);
            Assert.AreEqual(1, BigEndianConverter.ToInt64(buffer, 0));
        }

        [TestMethod]
        public void StreamOptimized_ReadSectors_FromTheMiddleOfACompressedGrainToItsEnd()
        {
            VirtualMachineDisk virtualMachineDisk = new VirtualMachineDisk(StreamOptimizedeVmdkPath);
            byte[] buffer = virtualMachineDisk.ReadSectors(1, 127);
            Assert.AreEqual(VirtualMachineDisk.BytesPerDiskSector * 127, buffer.Length);
            Assert.AreEqual(1, BigEndianConverter.ToInt64(buffer, 0));
        }

        [TestMethod]
        public void StreamOptimized_ReadSectors_FromTheMiddleOfACompressedGrainToTheMiddleOfNextGrain()
        {
            VirtualMachineDisk virtualMachineDisk = new VirtualMachineDisk(StreamOptimizedeVmdkPath);
            byte[] buffer = virtualMachineDisk.ReadSectors(1, 128);
            Assert.AreEqual(VirtualMachineDisk.BytesPerDiskSector * 128, buffer.Length);
            Assert.AreEqual(1, BigEndianConverter.ToInt64(buffer, 0));
        }

        [TestMethod]
        public void StreamOptimized_ReadSectors_FromTheMiddleOfACompressedGrainToTheEndOfNextGrain()
        {
            VirtualMachineDisk virtualMachineDisk = new VirtualMachineDisk(StreamOptimizedeVmdkPath);
            byte[] buffer = virtualMachineDisk.ReadSectors(1, 255);
            Assert.AreEqual(VirtualMachineDisk.BytesPerDiskSector * 255, buffer.Length);
            Assert.AreEqual(1, BigEndianConverter.ToInt64(buffer, 0));
        }

        private void TestDiskContent(VirtualMachineDisk virtualMachineDisk)
        {
            int readSizeInSectors = 128;
            byte[] buffer = virtualMachineDisk.ReadSectors(0, readSizeInSectors);
            Assert.AreEqual(1, BigEndianConverter.ToInt64(buffer, 512));
            Assert.AreEqual(2, BigEndianConverter.ToInt64(buffer, 512 * 2));
            Assert.AreEqual(3, BigEndianConverter.ToInt64(buffer, 512 * 3));

            BigEndianWriter.WriteUInt64(buffer, 512, 0);
            BigEndianWriter.WriteUInt64(buffer, 512 * 2, 0);
            BigEndianWriter.WriteUInt64(buffer, 512 * 3, 0);

            byte[] empty = new byte[VirtualMachineDisk.BytesPerDiskSector * readSizeInSectors];
            Assert.IsTrue(ByteUtils.AreByteArraysEqual(empty, buffer));
            for (int readIndex = 1; readIndex < 512; readIndex++)
            {
                buffer = virtualMachineDisk.ReadSectors(readIndex * readSizeInSectors, readSizeInSectors);
                Assert.IsTrue(ByteUtils.AreByteArraysEqual(empty, buffer));
            }

            buffer = virtualMachineDisk.ReadSectors(64 * 1024, 128); // 32 MB offset, sector 0x10000
            Assert.AreEqual(0x10000, BigEndianConverter.ToInt64(buffer, 0));
            Assert.AreEqual(0x10001, BigEndianConverter.ToInt64(buffer, 512));
            Assert.AreEqual(0x10002, BigEndianConverter.ToInt64(buffer, 512 * 2));
            Assert.AreEqual(0x10003, BigEndianConverter.ToInt64(buffer, 512 * 3));

            BigEndianWriter.WriteUInt64(buffer, 0, 0);
            BigEndianWriter.WriteUInt64(buffer, 512, 0);
            BigEndianWriter.WriteUInt64(buffer, 512 * 2, 0);
            BigEndianWriter.WriteUInt64(buffer, 512 * 3, 0);
            Assert.IsTrue(ByteUtils.AreByteArraysEqual(empty, buffer));

            for (int readIndex = 513; readIndex < 163840; readIndex++)
            {
                buffer = virtualMachineDisk.ReadSectors(readIndex * readSizeInSectors, readSizeInSectors);
                Assert.IsTrue(ByteUtils.AreByteArraysEqual(empty, buffer));
            }
        }
    }
}
