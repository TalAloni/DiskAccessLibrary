﻿/* Copyright (C) 2014-2025 Tal Aloni <tal.aloni.il@gmail.com>. All rights reserved.
 * 
 * You can redistribute this program and/or modify it under the terms of
 * the GNU Lesser Public License as published by the Free Software Foundation,
 * either version 3 of the License, or (at your option) any later version.
 */
using DiskAccessLibrary.VMDK;
using System;
using Utilities;

namespace DiskAccessLibrary
{
    public partial class VirtualMachineDisk
    {
        public static VirtualMachineDisk CreateMonolithicFlat(string path, long size)
        {
            string directory = System.IO.Path.GetDirectoryName(path);
            string extentFileName = System.IO.Path.GetFileNameWithoutExtension(path) + "-flat.vmdk";
            string extentPath = System.IO.Path.Combine(directory, extentFileName);
            RawDiskImage.Create(extentPath, size);

            VirtualMachineDiskDescriptor descriptor = VirtualMachineDiskDescriptor.CreateDescriptor(VirtualMachineDiskType.MonolithicFlat, size);
            VirtualMachineDiskExtentEntry extentEntry = CreateExtentEntry(size, ExtentType.Flat, extentFileName);
            descriptor.ExtentEntries.Add(extentEntry);
            descriptor.SaveToFile(path);

            return new VirtualMachineDisk(path);
        }

        public static VirtualMachineDisk CreateMonolithicSparse(string path, long size)
        {
            VirtualMachineDiskDescriptor descriptor = VirtualMachineDiskDescriptor.CreateDescriptor(VirtualMachineDiskType.MonolithicSparse, size);
            string fileName = System.IO.Path.GetFileName(path);

            VirtualMachineDiskExtentEntry extentEntry = CreateExtentEntry(size, ExtentType.Sparse, fileName);
            descriptor.ExtentEntries.Add(extentEntry);

            byte[] descriptorBytes = descriptor.GetDescriptorBytes();
            // VMWare products will leave "room for growth" for the descriptor
            int desiredDescriptorLength = Math.Max(descriptorBytes.Length, 20 * BytesPerDiskSector);
            int descriptorSizeInSectors = (int)Math.Ceiling((decimal)desiredDescriptorLength / BytesPerDiskSector);
            descriptorBytes = ByteUtils.Concatenate(descriptorBytes, new byte[descriptorSizeInSectors * BytesPerDiskSector - descriptorBytes.Length]);

            long sizeInSectors = size / BytesPerDiskSector;
            int grainSizeInSectors = 128;
            int numberOfSectorsRepresentedByGrainTable = SparseExtentHeader.NumberOfGrainTableEntriesPerGrainTable * grainSizeInSectors;
            int numberOfGrainTables = (int)Math.Ceiling((decimal)sizeInSectors / numberOfSectorsRepresentedByGrainTable);
            int grainDirectorySizeInSectors = (int)Math.Ceiling((decimal)numberOfGrainTables * 4 / BytesPerDiskSector);
            int grainTableSizeInSectors = SparseExtentHeader.NumberOfGrainTableEntriesPerGrainTable * 4 / BytesPerDiskSector; // Always 4
            int grainTableArraySizeInSectors = numberOfGrainTables * grainTableSizeInSectors;
            int grainTableArrayPaddingSizeInSectors = (int)Math.Ceiling((decimal)grainTableArraySizeInSectors / grainSizeInSectors) * grainSizeInSectors - grainTableArraySizeInSectors;
            int grainMetadataSizeInSectors = (grainDirectorySizeInSectors + grainTableArraySizeInSectors + grainTableArrayPaddingSizeInSectors) * 2;

            int sparseMetadataSizeInSectorsWithoutPadding = SparseExtentHeader.Length / BytesPerDiskSector + descriptorSizeInSectors + grainMetadataSizeInSectors;
            // We add padding for grain boundary alignment for the first grain
            int sparseMetadataSizeInSectorsIncludingPadding = (int)Math.Ceiling((decimal)sparseMetadataSizeInSectorsWithoutPadding / grainSizeInSectors) * grainSizeInSectors;

            long redundantGrainDirectorySectorIndex = 1 + descriptorSizeInSectors;
            long grainDirectorySectorIndex = redundantGrainDirectorySectorIndex + grainDirectorySizeInSectors + numberOfGrainTables * grainTableSizeInSectors + grainTableArrayPaddingSizeInSectors;
            SparseExtentHeader sparseExtentHeader = new SparseExtentHeader((ulong)sizeInSectors, (ulong)grainSizeInSectors, (ulong)(descriptorBytes.Length / BytesPerDiskSector));
            sparseExtentHeader.HasRedundantGrainTable = true;
            sparseExtentHeader.DescriptorOffset = 1;
            sparseExtentHeader.RedundantGDOffset = (ulong)redundantGrainDirectorySectorIndex;
            sparseExtentHeader.GDOffset = (ulong)grainDirectorySectorIndex;
            sparseExtentHeader.OverHead = (ulong)sparseMetadataSizeInSectorsIncludingPadding;

            RawDiskImage sparseExtentImage = RawDiskImage.Create(path, sparseMetadataSizeInSectorsIncludingPadding * BytesPerDiskSector);
            sparseExtentImage.WriteSectors(0, sparseExtentHeader.GetBytes());
            sparseExtentImage.WriteSectors(1, descriptorBytes);
            WriteGrainMetadata(sparseExtentImage, redundantGrainDirectorySectorIndex, numberOfGrainTables);
            WritePadding(sparseExtentImage, redundantGrainDirectorySectorIndex + grainDirectorySizeInSectors + grainTableArraySizeInSectors, grainTableArrayPaddingSizeInSectors);
            WriteGrainMetadata(sparseExtentImage, grainDirectorySectorIndex, numberOfGrainTables);
            WritePadding(sparseExtentImage, grainDirectorySectorIndex + grainDirectorySizeInSectors + grainTableArraySizeInSectors, grainTableArrayPaddingSizeInSectors);
            WritePadding(sparseExtentImage, sparseMetadataSizeInSectorsWithoutPadding, sparseMetadataSizeInSectorsIncludingPadding - sparseMetadataSizeInSectorsWithoutPadding);
            return new VirtualMachineDisk(path);
        }

        public static VirtualMachineDisk CreateStreamOptimized(string path, long size)
        {
            return CreateStreamOptimized(path, size, true);
        }

        /// <summary>
        /// Stream-optimized VMDK does not support random write access.
        /// The location of each subsequent write must be equal to or larger than the end of the last write.
        /// ExclusiveLock must be called before writing, and ReleaseLock must be called for grain directory and footer to be written.
        /// </summary>
        public static VirtualMachineDisk CreateStreamOptimized(string path, long size, bool useFastestCompression)
        {
            VirtualMachineDiskDescriptor descriptor = VirtualMachineDiskDescriptor.CreateDescriptor(VirtualMachineDiskType.StreamOptimized, size);
            string fileName = System.IO.Path.GetFileName(path);

            VirtualMachineDiskExtentEntry extentEntry = CreateExtentEntry(size, ExtentType.Sparse, fileName);
            extentEntry.WriteAccess = false;
            descriptor.ExtentEntries.Add(extentEntry);

            byte[] descriptorBytes = descriptor.GetDescriptorBytes();
            int descriptorLength = descriptorBytes.Length;
            int descriptorSizeInSectors = (int)Math.Ceiling((decimal)descriptorLength / BytesPerDiskSector);
            int paddedDescriptorLength = Math.Max(descriptorBytes.Length, 127 * BytesPerDiskSector);
            int paddedDescriptorSizeInSectors = (int)Math.Ceiling((decimal)paddedDescriptorLength / BytesPerDiskSector);
            descriptorBytes = ByteUtils.Concatenate(descriptorBytes, new byte[paddedDescriptorSizeInSectors * BytesPerDiskSector - descriptorBytes.Length]);

            long sizeInSectors = size / BytesPerDiskSector;
            int grainSizeInSectors = 128;

            SparseExtentHeader sparseExtentHeader = new SparseExtentHeader((ulong)sizeInSectors, (ulong)grainSizeInSectors, (ulong)descriptorSizeInSectors);
            sparseExtentHeader.UseCompressionForGrains = true;
            sparseExtentHeader.HasMarkers = true;
            sparseExtentHeader.Version = 3;
            sparseExtentHeader.DescriptorOffset = 1;
            sparseExtentHeader.GDOffset = 0xFFFFFFFFFFFFFFFF;
            sparseExtentHeader.OverHead = 0x80;
            sparseExtentHeader.CompressionAlgirithm = SparseExtentCompression.Deflate;

            RawDiskImage sparseExtentImage = RawDiskImage.Create(path, (1 + paddedDescriptorSizeInSectors) * BytesPerDiskSector);
            sparseExtentImage.WriteSectors(0, sparseExtentHeader.GetBytes());
            sparseExtentImage.WriteSectors(1, descriptorBytes);

            VirtualMachineDisk virtualMachineDisk = new VirtualMachineDisk(path);
            ((StreamOptimizedWriteOnlySparseExtent)virtualMachineDisk.Extent).UseFastestCompression = useFastestCompression;
            return virtualMachineDisk;
        }

        private static VirtualMachineDiskExtentEntry CreateExtentEntry(long extentSize, ExtentType extentType, string extentFileName)
        {
            VirtualMachineDiskExtentEntry extentEntry = new VirtualMachineDiskExtentEntry();
            extentEntry.ReadAccess = true;
            extentEntry.WriteAccess = true;
            extentEntry.SizeInSectors = extentSize / BytesPerDiskSector;
            extentEntry.ExtentType = extentType;
            extentEntry.FileName = extentFileName;
            if (extentType == ExtentType.Flat)
            {
                extentEntry.Offset = 0;
            }

            return extentEntry;
        }

        private static void WriteGrainMetadata(RawDiskImage sparseExtentImage, long sectorIndex, int numberOfGrainTables)
        {
            // Each grain table contains 512 entries (NumGTEsPerGT).
            // GrainSize is usually set to 128 sectors (64 KB), so each grain table represents 32 MB of disk space.
            // This means that each sector of a grain directory represents 4 GB of disk space.
            // Thus, even for 2 TB disks, the grain directory will be 256 KB in size, a size we can store entirely in RAM without an issue.
            int grainDirectorySizeInSectors = (int)Math.Ceiling((decimal)numberOfGrainTables * 4 / BytesPerDiskSector);
            int grainTableSizeInSectors = SparseExtentHeader.NumberOfGrainTableEntriesPerGrainTable * 4 / BytesPerDiskSector; // Always 4
            byte[] grainDirectory = new byte[grainDirectorySizeInSectors * BytesPerDiskSector];
            for (int grainTableIndex = 0; grainTableIndex < grainDirectory.Length / 4; grainTableIndex++)
            {
                // Similar to VMWare products, we will also set the grain directory "padding" as per the assumption that the disk will be extended.
                uint grainTableSectorIndex = (uint)(sectorIndex + grainDirectorySizeInSectors + grainTableIndex * grainTableSizeInSectors);
                LittleEndianWriter.WriteUInt32(grainDirectory, grainTableIndex * 4, grainTableSectorIndex);
            }

            sparseExtentImage.WriteSectors(sectorIndex, grainDirectory);

            byte[] emptyGrainTable = new byte[SparseExtentHeader.NumberOfGrainTableEntriesPerGrainTable * 4];
            for (int grainTableIndex = 0; grainTableIndex < numberOfGrainTables; grainTableIndex++)
            {
                uint grainTableSectorIndex = (uint)(sectorIndex + grainDirectorySizeInSectors + grainTableIndex * grainTableSizeInSectors);
                sparseExtentImage.WriteSectors(grainTableSectorIndex, emptyGrainTable);
            }
        }

        private static void WritePadding(RawDiskImage sparseExtentImage, long sectorIndex, int sectorCount)
        {
            byte[] padding = new byte[sectorCount * BytesPerDiskSector];
            sparseExtentImage.WriteSectors(sectorIndex, padding);
        }

        // https://kb.vmware.com/s/article/1026266
        public static void GetDiskGeometry(long totalSectors, out byte heads, out byte sectorsPerTrack, out long cylinders)
        {
            if (totalSectors * BytesPerDiskSector < 1073741824) // < 1 GB
            {
                heads = 64;
                sectorsPerTrack = 32;
            }
            else if (totalSectors * BytesPerDiskSector < 2147483648) // < 2 GB
            {
                heads = 128;
                sectorsPerTrack = 32;
            }
            else
            {
                heads = 255;
                sectorsPerTrack = 63;
            }
            cylinders = totalSectors / (heads * sectorsPerTrack);
        }
    }
}
