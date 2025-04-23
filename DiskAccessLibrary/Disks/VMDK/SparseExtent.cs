/* Copyright (C) 2014-2025 Tal Aloni <tal.aloni.il@gmail.com>. All rights reserved.
 * 
 * You can redistribute this program and/or modify it under the terms of
 * the GNU Lesser Public License as published by the Free Software Foundation,
 * either version 3 of the License, or (at your option) any later version.
 */
using System;
using System.Collections.Generic;
using System.Text;
using Utilities;

namespace DiskAccessLibrary.VMDK
{
    public partial class SparseExtent : DiskImage
    {
        private RawDiskImage m_file;
        private SparseExtentHeader m_header;
        private VirtualMachineDiskDescriptor m_descriptor;
        private byte[] m_grainDirectoryBytes;
        private byte[] m_redundantGrainDirectoryBytes;
        private long m_sectorsToAllocate = 0;

        protected internal SparseExtent(RawDiskImage file, SparseExtentHeader header) : base(file.Path, file.IsReadOnly)
        {
            m_file = file;
            m_header = header;
            ReadEmbeddedDescriptor();
        }

        private void ReadEmbeddedDescriptor()
        {
            if (m_header.DescriptorOffset > 0)
            {
                byte[] descriptorBytes = m_file.ReadSectors((long)m_header.DescriptorOffset, (int)m_header.DescriptorSize);
                string text = ASCIIEncoding.ASCII.GetString(descriptorBytes);
                List<string> lines = VirtualMachineDiskDescriptor.GetLines(text);
                m_descriptor = new VirtualMachineDiskDescriptor(lines);

                if (m_descriptor.DiskType == VirtualMachineDiskType.StreamOptimized)
                {
                    if (!(this is StreamOptimizedWriteOnlySparseExtent)) // In new stream-optimized sparse extent, the footer has not been written yet
                    {
                        // Read the footer which contains the grainTableOffset
                        byte[] footerBytes = m_file.ReadSector(m_file.TotalSectors - 2);
                        m_header = new SparseExtentHeader(footerBytes);
                    }
                }
            }
        }

        public override bool ExclusiveLock()
        {
            return m_file.ExclusiveLock();
        }

        public override bool ExclusiveLock(bool useOverlappedIO)
        {
            return m_file.ExclusiveLock(useOverlappedIO);
        }

        public override bool ReleaseLock()
        {
            return m_file.ReleaseLock();
        }

        private byte[] ReadGrainDirectoryBytes(bool useRedundant)
        {
            ulong grainTableOffset = useRedundant? m_header.RedundantGDOffset : m_header.GDOffset;
            ulong numberOfGrains = m_header.Capacity / m_header.GrainSize;
            int numberOfGrainTables = (int)Math.Ceiling((double)numberOfGrains / m_header.NumGTEsPerGT);
            int grainDirectorySizeInBytes = numberOfGrainTables * 4;
            int grainDirectorySizeInSectors = (int)Math.Ceiling((double)grainDirectorySizeInBytes / BytesPerSector);
            return m_file.ReadSectors((long)grainTableOffset, grainDirectorySizeInSectors);
        }

        private void ReadGrainDirectory()
        {
            m_grainDirectoryBytes = ReadGrainDirectoryBytes(false);
        }

        private void ReadRedundantGrainDirectory()
        {
            m_redundantGrainDirectoryBytes = ReadGrainDirectoryBytes(true);
        }

        private KeyValuePairList<long, int> MapSectors(long sectorIndex, int sectorCount)
        {
            return MapSectors(sectorIndex, sectorCount, false, null);
        }

        private KeyValuePairList<long, int> MapSectors(long sectorIndex, int sectorCount, bool allocateUnmappedSectors, byte[] data)
        {
            if (m_grainDirectoryBytes == null)
            {
                ReadGrainDirectory();
            }

            long grainIndex = sectorIndex / (long)m_header.GrainSize;
            int grainTableEntriesPerSector = BytesPerSector / 4;
            int grainTableIndex = (int)(grainIndex / m_header.NumGTEsPerGT); // The index in the grain directory
            uint grainTableStartSectorIndex = LittleEndianConverter.ToUInt32(m_grainDirectoryBytes, grainTableIndex * 4);
            int offsetInGrainTable = (int)(grainIndex % m_header.NumGTEsPerGT);
            int grainTableSectorOffset = offsetInGrainTable / grainTableEntriesPerSector; // The sector offset in the table containing the grain entry corresponding to sectorIndex
            int grainIndexInBuffer = (int)grainIndex % grainTableEntriesPerSector;
            int grainTableEntriesToReadFromTable = (int)Math.Ceiling((double)sectorCount / grainTableEntriesPerSector);
            int sectorsToReadFromTable = 1 + (int)Math.Floor((double)(grainIndexInBuffer + grainTableEntriesToReadFromTable - 1) / grainTableEntriesPerSector);

            if (m_descriptor.DiskType == VirtualMachineDiskType.StreamOptimized)
            {
                // For MonolithicSparse we assume that the grain table array is consecutive.
                // (for MonolithicSparse it is not even necessary to read the entire grain directory)
                int numberOfSectorsPerGT = (int)(m_header.NumGTEsPerGT * 4 / BytesPerSector);
                if (sectorsToReadFromTable - grainTableSectorOffset > numberOfSectorsPerGT)
                {
                    throw new NotImplementedException("A single read/write cannot exceed grain table boundary");
                }
            }

            if (grainTableStartSectorIndex == 0)
            {
                // Indicates sparse grain table, might be related to the undocumented sparse extent header v3
                return new KeyValuePairList<long, int>()
                {
                    new KeyValuePair<long, int>(0, sectorCount)
                };
            }

            byte[] grainTableBuffer = m_file.ReadSectors(grainTableStartSectorIndex + grainTableSectorOffset, sectorsToReadFromTable);

            long sectorIndexInGrain = sectorIndex % (long)m_header.GrainSize;
            if (sectorIndexInGrain > 0 && m_header.UseCompressionForGrains)
            {
                throw new NotImplementedException("Read cannot start from the middle of a compressed grain");
            }

            KeyValuePairList<long, int> result = new KeyValuePairList<long, int>();
            uint grainOffset = LittleEndianConverter.ToUInt32(grainTableBuffer, grainIndexInBuffer * 4);
            int sectorsLeft = sectorCount;
            int sectorsProcessedInGrain = (int)Math.Min(sectorsLeft, (long)m_header.GrainSize - sectorIndexInGrain);
            bool updateGrainTableArrays = false;
            if (grainOffset == 0 && allocateUnmappedSectors)
            {
                if (!IsAllZeros(data, 0, sectorsProcessedInGrain * BytesPerSector)) // No need to allocate grain to write zeros
                {
                    grainOffset = GetNextAllocatedGrainSectorOffset();
                    LittleEndianWriter.WriteUInt32(grainTableBuffer, grainIndexInBuffer * 4, grainOffset);
                    updateGrainTableArrays = true;
                }
            }

            if (grainOffset != 0)
            {
                grainOffset += (uint)sectorIndexInGrain;
            }

            result.Add(grainOffset, sectorsProcessedInGrain);
            sectorsLeft -= sectorsProcessedInGrain;

            while (sectorsLeft > 0)
            {
                grainIndexInBuffer++;
                grainOffset = LittleEndianConverter.ToUInt32(grainTableBuffer, grainIndexInBuffer * 4);
                sectorsProcessedInGrain = (int)Math.Min(sectorsLeft, (long)m_header.GrainSize);
                if (grainOffset == 0 && allocateUnmappedSectors)
                {
                    if (!IsAllZeros(data, (sectorCount - sectorsLeft) * BytesPerSector, sectorsProcessedInGrain * BytesPerSector)) // No need to allocate grain to write zeros
                    {
                        grainOffset = GetNextAllocatedGrainSectorOffset();
                        LittleEndianWriter.WriteUInt32(grainTableBuffer, grainIndexInBuffer * 4, grainOffset);
                        updateGrainTableArrays = true;
                    }
                }

                long lastSectorIndex = result[result.Count - 1].Key;
                int lastSectorCount = result[result.Count - 1].Value;
                if (((lastSectorIndex == 0 && grainOffset == 0) || lastSectorIndex + lastSectorCount == grainOffset) &&
                    !m_header.UseCompressionForGrains)
                {
                    // Note: For compression we want the caller to process each grain separately
                    result[result.Count - 1] = new KeyValuePair<long, int>(lastSectorIndex, lastSectorCount + sectorsProcessedInGrain);
                }
                else
                {
                    result.Add(grainOffset, sectorsProcessedInGrain);
                }
                sectorsLeft -= sectorsProcessedInGrain;
            }

            AllocateGrains();
            if (updateGrainTableArrays) // Update grain table to point to newly allocated grains
            {
                m_file.WriteSectors(grainTableStartSectorIndex + grainTableSectorOffset, grainTableBuffer);

                if (m_header.HasRedundantGrainTable)
                {
                    if (m_redundantGrainDirectoryBytes == null)
                    {
                        ReadRedundantGrainDirectory();
                    }
                    uint redundantGrainTableStartSectorIndex = LittleEndianConverter.ToUInt32(m_redundantGrainDirectoryBytes, grainTableIndex * 4);
                    m_file.WriteSectors(redundantGrainTableStartSectorIndex + grainTableSectorOffset, grainTableBuffer);
                }
            }

            return result;
        }

        private uint GetNextAllocatedGrainSectorOffset()
        {
            uint grainOffet = (uint)(m_file.TotalSectors + m_sectorsToAllocate);
            m_sectorsToAllocate += (long)m_header.GrainSize;
            return grainOffet;
        }

        private void AllocateGrains()
        {
            m_file.Extend(m_sectorsToAllocate * BytesPerSector);
            m_sectorsToAllocate = 0;
        }

        public override byte[] ReadSectors(long sectorIndex, int sectorCount)
        {
            CheckBoundaries(sectorIndex, sectorCount);
            byte[] result = new byte[sectorCount * this.BytesPerSector];
            int offset = 0;
            int offsetFromGrainStartInBytes = 0;
            if (m_header.UseCompressionForGrains)
            {
                int offsetFromGrainStartInSectors = (int)((ulong)sectorIndex % m_header.GrainSize);
                sectorIndex -= offsetFromGrainStartInSectors;
                sectorCount += offsetFromGrainStartInSectors;
                offsetFromGrainStartInBytes = offsetFromGrainStartInSectors * BytesPerSector;
            }
            KeyValuePairList<long, int> map = MapSectors(sectorIndex, sectorCount);
            for (int entryIndex = 0; entryIndex < map.Count; entryIndex++)
            {
                KeyValuePair<long, int> entry = map[entryIndex];
                byte[] readBuffer;
                int readSize = entry.Value * this.BytesPerSector;
                if (entryIndex == 0)
                {
                    readSize -= offsetFromGrainStartInBytes;
                }

                bool isGrainAllocated = entry.Key != 0;
                if (isGrainAllocated)
                {
                    if (!m_header.UseCompressionForGrains)
                    {
                        readBuffer = m_file.ReadSectors(entry.Key, entry.Value);
                    }
                    else
                    {
                        readBuffer = m_file.ReadSector(entry.Key);
                        uint compressedSize = LittleEndianConverter.ToUInt32(readBuffer, 8);
                        int grainMarkerSize = 12;
                        int sectorsToRead = (int)Math.Ceiling((double)(grainMarkerSize + compressedSize) / BytesPerSector);
                        if (sectorsToRead > 1)
                        {
                            readBuffer = ByteUtils.Concatenate(readBuffer, m_file.ReadSectors(entry.Key + 1, sectorsToRead - 1));
                        }
                        readBuffer = CompressionHelper.Decompress(readBuffer, grainMarkerSize, (int)m_header.GrainSize * BytesPerSector);
                    }

                    int readStartOffset = entryIndex == 0 ? offsetFromGrainStartInBytes : 0;
                    Array.Copy(readBuffer, readStartOffset, result, offset, readSize);
                }
                offset += readSize;
            }

            return result;
        }

        public override void WriteSectors(long sectorIndex, byte[] data)
        {
            if (IsReadOnly)
            {
                throw new UnauthorizedAccessException("Attempted to perform write on a readonly disk");
            }

            if (m_header.UseCompressionForGrains)
            {
                throw new NotSupportedException("Writing to compressed sparse extent is not supported");
            }

            int sectorCount = data.Length / BytesPerSector;
            CheckBoundaries(sectorIndex, sectorCount);

            KeyValuePairList<long, int> map = MapSectors(sectorIndex, sectorCount, true, data);
            int offset = 0;
            foreach (KeyValuePair<long, int> entry in map)
            {
                bool isWriteNeeded = entry.Key != 0;
                int writeSize = entry.Value * BytesPerSector;
                if (isWriteNeeded)
                {
                    byte[] temp = new byte[writeSize];
                    Array.Copy(data, offset, temp, 0, temp.Length);
                    m_file.WriteSectors(entry.Key, temp);
                }
                offset += writeSize;
            }
        }

        public override void Extend(long numberOfAdditionalBytes)
        {
            throw new NotImplementedException("The method or operation is not implemented.");
        }

        public override int BytesPerSector
        {
            get
            {
                return VirtualMachineDisk.BytesPerDiskSector;
            }
        }

        public override long Size
        {
            get
            {
                return (long)(m_header.Capacity * (ulong)this.BytesPerSector);
            }
        }

        public VirtualMachineDiskDescriptor Descriptor
        {
            get
            {
                return m_descriptor;
            }
        }

        protected internal SparseExtentHeader Header
        {
            get
            {
                return m_header;
            }
        }

        protected internal RawDiskImage File
        {
            get
            {
                return m_file;
            }
        }

        public static SparseExtent OpenSparseExtent(string path, bool isReadOnly)
        {
            RawDiskImage file = new RawDiskImage(path, VirtualMachineDisk.BytesPerDiskSector, isReadOnly);
            byte[] headerBytes = file.ReadSector(0);
            SparseExtentHeader header = new SparseExtentHeader(headerBytes);
            if (!header.IsSupported)
            {
                throw new NotSupportedException("Sparse extent header version is not supported");
            }

            if ((header.Flags & SparseExtentHeaderFlags.UseZeroedGrainGTEs) > 0)
            {
                throw new NotSupportedException("Zeroed grain GTEs are not supported");
            }

            if ((long)header.OverHead == file.TotalSectors && header.UseCompressionForGrains && header.HasMarkers)
            {
                // Special case: New stream-optimized sparse extent
                return new StreamOptimizedWriteOnlySparseExtent(file, header);
            }
            else
            {
                return new SparseExtent(file, header);
            }
        }

        protected static bool IsAllZeros(byte[] array, int offset, int count)
        {
            for (int index = 0; index < count; index++)
            {
                if (array[offset + index] != 0)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
