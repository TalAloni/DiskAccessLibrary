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
        private bool m_isReadOnly;
        private SparseExtentHeader m_header;
        private VirtualMachineDiskDescriptor m_descriptor;
        private byte[] m_grainDirectoryBytes;
        private byte[] m_redundantGrainDirectoryBytes;

        public SparseExtent(string path) : this(path, false)
        {
        }

        public SparseExtent(string path, bool isReadOnly) : base(path)
        {
            m_file = new RawDiskImage(path, VirtualMachineDisk.BytesPerDiskSector, isReadOnly);
            m_isReadOnly = isReadOnly;
            byte[] headerBytes = m_file.ReadSector(0);
            m_header = new SparseExtentHeader(headerBytes);
            if (!m_header.IsSupported)
            {
                throw new NotSupportedException("Sparse extent header version is not supported");
            }

            if ((m_header.Flags & SparseExtentHeaderFlags.UseZeroedGrainGTEs) > 0)
            {
                throw new NotSupportedException("Zeroed grain GTEs are not supported");
            }

            if (m_header.CompressionAlgirithm != SparseExtentCompression.None)
            {
                throw new NotSupportedException("Sparse extent compression is not supported");
            }

            if (m_header.DescriptorOffset > 0)
            {
                byte[] descriptorBytes = m_file.ReadSectors((long)m_header.DescriptorOffset, (int)m_header.DescriptorSize);
                string text = ASCIIEncoding.ASCII.GetString(descriptorBytes);
                List<string> lines = VirtualMachineDiskDescriptor.GetLines(text);
                m_descriptor = new VirtualMachineDiskDescriptor(lines);

                if (m_descriptor.DiskType == VirtualMachineDiskType.StreamOptimized)
                {
                    // Read the footer which contains the grainTableOffset
                    byte[] footerBytes = m_file.ReadSector(m_file.TotalSectors - 2);
                    m_header = new SparseExtentHeader(footerBytes);
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

        private KeyValuePairList<long, int> MapSectors(long sectorIndex, int sectorCount, bool allocateUnmappedSectors)
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
            byte[] grainTableBuffer = m_file.ReadSectors(grainTableStartSectorIndex + grainTableSectorOffset, sectorsToReadFromTable);

            long sectorIndexInGrain = sectorIndex % (long)m_header.GrainSize;
            if (sectorIndexInGrain > 0 && m_header.UseCompressionForGrains)
            {
                throw new NotImplementedException("Read cannot start from the middle of a compressed grain");
            }

            KeyValuePairList<long, int> result = new KeyValuePairList<long, int>();
            uint grainOffset = LittleEndianConverter.ToUInt32(grainTableBuffer, grainIndexInBuffer * 4);
            bool updateGrainTableArrays = false;
            if (grainOffset == 0 && allocateUnmappedSectors)
            {
                grainOffset = AllocateGrain();
                LittleEndianWriter.WriteUInt32(grainTableBuffer, grainIndexInBuffer * 4, grainOffset);
                updateGrainTableArrays = true;
            }
            grainOffset += (uint)sectorIndexInGrain;
            int sectorsLeft = sectorCount;
            int sectorsProcessedInGrain = (int)Math.Min(sectorsLeft, (long)m_header.GrainSize - sectorIndexInGrain);
            result.Add(grainOffset, sectorsProcessedInGrain);
            sectorsLeft -= sectorsProcessedInGrain;

            while (sectorsLeft > 0)
            {
                grainIndexInBuffer++;
                grainOffset = LittleEndianConverter.ToUInt32(grainTableBuffer, grainIndexInBuffer * 4);
                if (grainOffset == 0 && allocateUnmappedSectors)
                {
                    grainOffset = AllocateGrain();
                    LittleEndianWriter.WriteUInt32(grainTableBuffer, grainIndexInBuffer * 4, grainOffset);
                    updateGrainTableArrays = true;
                }
                sectorsProcessedInGrain = (int)Math.Min(sectorsLeft, (long)m_header.GrainSize);
                long lastSectorIndex = result[result.Count - 1].Key;
                int lastSectorCount = result[result.Count - 1].Value;
                if (lastSectorIndex + lastSectorCount == grainOffset && !m_header.UseCompressionForGrains)
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

            // Allocate unallocated grains
            if (updateGrainTableArrays)
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

        private uint AllocateGrain()
        {
            uint grainOffet = (uint)m_file.TotalSectors;
            m_file.Extend((long)m_header.GrainSize * BytesPerSector);
            return grainOffet;
        }

        public override byte[] ReadSectors(long sectorIndex, int sectorCount)
        {
            CheckBoundaries(sectorIndex, sectorCount);
            byte[] result = new byte[sectorCount * this.BytesPerSector];
            int offset = 0;
            KeyValuePairList<long, int> map = MapSectors(sectorIndex, sectorCount, false);
            foreach (KeyValuePair<long, int> entry in map)
            {
                byte[] temp;
                if (entry.Key == 0) // 0 means that the grain is not yet allocated
                {
                    temp = new byte[entry.Value * this.BytesPerSector];
                }
                else
                {
                    temp = m_file.ReadSectors(entry.Key, entry.Value);
                }
                Array.Copy(temp, 0, result, offset, temp.Length);
                offset += temp.Length;
            }

            return result;
        }

        public override void WriteSectors(long sectorIndex, byte[] data)
        {
            if (m_isReadOnly)
            {
                throw new UnauthorizedAccessException("Attempted to perform write on a readonly disk");
            }

            int sectorCount = data.Length / BytesPerSector;
            CheckBoundaries(sectorIndex, sectorCount);

            KeyValuePairList<long, int> map = MapSectors(sectorIndex, sectorCount, true);
            int offset = 0;
            foreach (KeyValuePair<long, int> entry in map)
            {
                byte[] temp = new byte[entry.Value * BytesPerSector];
                Array.Copy(data, offset, temp, 0, temp.Length);
                m_file.WriteSectors(entry.Key, temp);
                offset += temp.Length;
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
    }
}
