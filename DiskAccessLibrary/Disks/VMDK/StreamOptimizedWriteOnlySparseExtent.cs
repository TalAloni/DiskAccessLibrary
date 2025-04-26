/* Copyright (C) 2025 Tal Aloni <tal.aloni.il@gmail.com>. All rights reserved.
 * 
 * You can redistribute this program and/or modify it under the terms of
 * the GNU Lesser Public License as published by the Free Software Foundation,
 * either version 3 of the License, or (at your option) any later version.
 */
using System;
using Utilities;

namespace DiskAccessLibrary.VMDK
{
    internal class StreamOptimizedWriteOnlySparseExtent : SparseExtent
    {
        private const uint MarkerEOS = 0;
        private const uint MarkerGT = 1;
        private const uint MarkerGD = 2;
        private const uint MarkerFooter = 3;

        private bool m_hasExclusiveLock = false;
        private long m_indexOfLastSectorWritten = -1;
        private int m_indexOfNextGrainTableToWrite = 0;
        private byte[] m_nextGrainTable;
        private long m_positionInFile; // In sectors
        private byte[] m_grainDirectory;
        private bool m_useFastestCompression = true;

        protected internal StreamOptimizedWriteOnlySparseExtent(RawDiskImage file, SparseExtentHeader header) : base(file, header)
        {
            m_positionInFile = file.TotalSectors;
            ulong numberOfGrains = Header.Capacity / Header.GrainSize;
            int numberOfGrainTables = (int)Math.Ceiling((double)numberOfGrains / Header.NumGTEsPerGT);
            m_grainDirectory = new byte[numberOfGrainTables * 4];
        }

        public override bool ExclusiveLock()
        {
            m_hasExclusiveLock = true;
            return base.ExclusiveLock();
        }

        public override bool ExclusiveLock(bool useOverlappedIO)
        {
            throw new NotSupportedException();
        }

        public override void Extend(long numberOfAdditionalBytes)
        {
            throw new NotSupportedException();
        }

        public override byte[] ReadSectors(long sectorIndex, int sectorCount)
        {
            throw new NotSupportedException();
        }

        public override bool ReleaseLock()
        {
            if (m_hasExclusiveLock)
            {
                WriteEndOfFile();
                m_hasExclusiveLock = false;
            }
            return base.ReleaseLock();
        }

        public override void WriteSectors(long sectorIndex, byte[] data)
        {
            if (!m_hasExclusiveLock)
            {
                throw new InvalidOperationException("Exclusive lock must be obtained before writing to a stream optimized VMDK");
            }

            if (sectorIndex <= m_indexOfLastSectorWritten)
            {
                throw new NotSupportedException("The location of each subsequent write must be equal to or larger than the end of the last write.");
            }

            if (sectorIndex % (int)Header.GrainSize > 0)
            {
                throw new NotSupportedException("Write must start from a beginning of a grain");
            }

            if (data.Length % (int)Header.GrainSize * BytesPerSector > 0)
            {
                throw new NotSupportedException("Data must be written in multiple of grain size");
            }

            long firstGrainIndex = sectorIndex / (long)Header.GrainSize;
            int sectorCount = data.Length / BytesPerSector;
            int grainCount = sectorCount / (int)Header.GrainSize;
            int firstGrainTableIndex = (int)(firstGrainIndex / (long)Header.NumGTEsPerGT); // The index in the grain directory
            int lastGrainTableIndex = (int)((long)(firstGrainIndex + grainCount - 1) / (long)Header.NumGTEsPerGT);
            if (firstGrainTableIndex != lastGrainTableIndex)
            {
                throw new NotSupportedException("Write cannot cross grain table boundary");
            }

            if (firstGrainTableIndex > m_indexOfNextGrainTableToWrite)
            {
                // Commit grain table
                WritePendingGrainTable();

                m_indexOfNextGrainTableToWrite = firstGrainTableIndex;
            }

            int firstGrainIndexInGrainTable = (int)(firstGrainIndex % Header.NumGTEsPerGT);
            long nextGrainWritePosition = m_positionInFile;
            byte[] buffer = new byte[0];
            int grainSizeInBytes = (int)Header.GrainSize * BytesPerSector;
            for (int grainOffset = 0; grainOffset < grainCount; grainOffset++)
            {
                int readOffset = grainSizeInBytes * grainOffset;
                if (!IsAllZeros(data, readOffset, grainSizeInBytes))
                {
                    if (m_nextGrainTable == null)
                    {
                        m_nextGrainTable = new byte[(int)Header.NumGTEsPerGT * 4];
                    }

                    LittleEndianWriter.WriteUInt32(m_nextGrainTable, (firstGrainIndexInGrainTable + grainOffset) * 4, (uint)nextGrainWritePosition);

                    byte[] compressedData = ZLibCompressionHelper.Compress(data, readOffset, grainSizeInBytes, m_useFastestCompression);
                    byte[] grainBytes = GetGrainBytes(sectorIndex + grainOffset * (int)Header.GrainSize, compressedData);
                    buffer = ByteUtils.Concatenate(buffer, grainBytes);

                    nextGrainWritePosition += grainBytes.Length / BytesPerSector;
                }
            }

            if (buffer.Length > 0)
            {
                AppendToFile(buffer);
            }

            m_indexOfLastSectorWritten = sectorIndex + sectorCount - 1;
        }

        private void WritePendingGrainTable()
        {
            if (m_nextGrainTable != null)
            {
                WriteGrainTable(m_indexOfNextGrainTableToWrite, m_nextGrainTable);
                m_nextGrainTable = null;
            }
        }

        private void WriteGrainTable(int grainTableIndex, byte[] grainTable)
        {
            // The grain directory entry points to the table, not the marker
            LittleEndianWriter.WriteUInt32(m_grainDirectory, grainTableIndex * 4, (uint)m_positionInFile + 1);

            byte[] grainTableWithMarker = new byte[BytesPerSector + grainTable.Length];
            LittleEndianWriter.WriteUInt64(grainTableWithMarker, 0, (ulong)(grainTable.Length / BytesPerSector));
            LittleEndianWriter.WriteUInt32(grainTableWithMarker, 12, MarkerGT);
            ByteWriter.WriteBytes(grainTableWithMarker, BytesPerSector, grainTable);

            AppendToFile(grainTableWithMarker);
        }

        private void WriteGrainDirectory()
        {
            int grainDirectorySectorCount = (int)Math.Ceiling((double)m_grainDirectory.Length / BytesPerSector);
            byte[] grainDirectoryWithMarker = new byte[(1 + grainDirectorySectorCount) * BytesPerSector];
            LittleEndianWriter.WriteUInt64(grainDirectoryWithMarker, 0, (ulong)grainDirectorySectorCount);
            LittleEndianWriter.WriteUInt32(grainDirectoryWithMarker, 12, MarkerGD);
            ByteWriter.WriteBytes(grainDirectoryWithMarker, BytesPerSector, m_grainDirectory);

            AppendToFile(grainDirectoryWithMarker);
        }

        private void WriteFooter(long grainDirectorySectorIndex)
        {
            byte[] footerWithMarker = new byte[BytesPerSector * 2];
            LittleEndianWriter.WriteUInt64(footerWithMarker, 0, 1);
            LittleEndianWriter.WriteUInt32(footerWithMarker, 12, MarkerFooter);

            Header.GDOffset = (ulong)grainDirectorySectorIndex;
            ByteWriter.WriteBytes(footerWithMarker, BytesPerSector, Header.GetBytes());

            AppendToFile(footerWithMarker);
        }

        private void WriteEndOfStreamMarker()
        {
            byte[] endOfStreamMarker = new byte[BytesPerSector];
            LittleEndianWriter.WriteUInt32(endOfStreamMarker, 12, MarkerEOS);

            AppendToFile(endOfStreamMarker);
        }

        private void WriteEndOfFile()
        {
            WritePendingGrainTable();
            // GDOffset points to the grain directory, not the marker
            long grainDirectorySectorIndex = m_positionInFile + 1;
            WriteGrainDirectory();
            WriteFooter(grainDirectorySectorIndex);
            WriteEndOfStreamMarker();
        }

        private void AppendToFile(byte[] data)
        {
            File.Extend(data.Length);
            File.WriteSectors(m_positionInFile, data);
            m_positionInFile += data.Length / BytesPerSector;
        }

        private byte[] GetGrainBytes(long sectorIndex, byte[] compressedData)
        {
            int markerLength = 12;
            int paddedLengthInSectors = (int)Math.Ceiling((double)(markerLength + compressedData.Length) / BytesPerSector);
            int paddedLengthInBytes = paddedLengthInSectors * BytesPerSector;
            byte[] buffer = new byte[paddedLengthInBytes];
            LittleEndianWriter.WriteUInt64(buffer, 0, (ulong)sectorIndex);
            LittleEndianWriter.WriteUInt32(buffer, 8, (uint)compressedData.Length);
            ByteWriter.WriteBytes(buffer, 12, compressedData);

            return buffer;
        }

        public bool UseFastestCompression
        {
            get
            {
                return m_useFastestCompression;
            }
            set
            {
                m_useFastestCompression = value;
            }
        }
    }
}
