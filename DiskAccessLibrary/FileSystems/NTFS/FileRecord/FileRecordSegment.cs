/* Copyright (C) 2014-2024 Tal Aloni <tal.aloni.il@gmail.com>. All rights reserved.
 * 
 * You can redistribute this program and/or modify it under the terms of
 * the GNU Lesser Public License as published by the Free Software Foundation,
 * either version 3 of the License, or (at your option) any later version.
 */
using System;
using System.Collections.Generic;
using System.IO;
using Utilities;

namespace DiskAccessLibrary.FileSystems.NTFS
{
    /// <summary>
    /// FILE_RECORD_SEGMENT_HEADER: https://msdn.microsoft.com/de-de/windows/desktop/bb470124
    /// </summary>
    /// <remarks>
    /// Attributes MUST be ordered by increasing attribute type code (with a secondary ordering by name) when written to disk.
    /// </remarks>
    public class FileRecordSegment
    {
        private const string ValidSignature = "FILE";
        private const int NTFS30UpdateSequenceArrayOffset = 0x2A; // NTFS v3.0 and earlier (up to Windows 2000)
        private const int NTFS31UpdateSequenceArrayOffset = 0x30; // NTFS v3.1 and later   (XP and later)
        private const uint AttributesEndMarker = 0xFFFFFFFF;
        private const int AttributesEndMarkerLength = 8; // The AttributesEndMarker is technically considered an attribute so it must be aligned to 8-byte boundary

        /* Start of FILE_RECORD_SEGMENT_HEADER */
        // MULTI_SECTOR_HEADER
        public ulong LogFileSequenceNumber; // LSN of last logged update to this file record segment
        private ushort m_sequenceNumber; // This value is incremented each time that a file record segment is freed
        public ushort ReferenceCount;
        // ushort FirstAttributeOffset;
        private FileRecordFlags m_flags;
        // uint SegmentLength; // FirstFreeByte, this value must always be aligned to 8-byte boundary (since each attribute record must be aligned to 8-byte boundary).
        private uint m_allocatedLength; // BytesAvailable
        private MftSegmentReference m_baseFileRecordSegment; // If this is the base file record, the value is 0
        public ushort NextAttributeInstance; // Starting from 0
        // 2 bytes padding
        private uint m_segmentNumberOnDisk; // Self-reference, NTFS v3.0+
        public ushort UpdateSequenceNumber; // a.k.a. USN
        // byte[] UpdateSequenceReplacementData
        /* End of FILE_RECORD_SEGMENT_HEADER */
        private List<AttributeRecord> m_immediateAttributes = new List<AttributeRecord>(); // Attribute records that are stored in the base file record

        private long m_segmentNumber; // We use our own segment number to support NTFS v3.0 (note that SegmentNumberOnDisk is UInt32, which is another reason to avoid it)

        public FileRecordSegment(long segmentNumber, ushort sequenceNumber) : this(segmentNumber, sequenceNumber, MftSegmentReference.NullReference)
        {
        }

        public FileRecordSegment(long segmentNumber, ushort sequenceNumber, MftSegmentReference baseFileRecordSegment)
        {
            m_segmentNumber = segmentNumber;
            m_sequenceNumber = sequenceNumber;
            m_baseFileRecordSegment = baseFileRecordSegment;
        }

        public FileRecordSegment(byte[] buffer, int offset, long segmentNumber)
        {
            MultiSectorHeader multiSectorHeader = new MultiSectorHeader(buffer, offset + 0x00);
            if (multiSectorHeader.Signature != ValidSignature)
            {
                throw new InvalidDataException("Invalid FILE record signature");
            }
            LogFileSequenceNumber = LittleEndianConverter.ToUInt64(buffer, offset + 0x08);
            m_sequenceNumber = LittleEndianConverter.ToUInt16(buffer, offset + 0x10);
            ReferenceCount = LittleEndianConverter.ToUInt16(buffer, offset + 0x12);
            ushort firstAttributeOffset = LittleEndianConverter.ToUInt16(buffer, offset + 0x14);
            m_flags = (FileRecordFlags)LittleEndianConverter.ToUInt16(buffer, offset + 0x16);
            uint segmentLength = LittleEndianConverter.ToUInt32(buffer, offset + 0x18);
            m_allocatedLength = LittleEndianConverter.ToUInt32(buffer, offset + 0x1C);
            if (segmentLength > m_allocatedLength)
            {
                throw new InvalidDataException("Invalid file record segment, record length is invalid");
            }
            m_baseFileRecordSegment = new MftSegmentReference(buffer, offset + 0x20);
            NextAttributeInstance = LittleEndianConverter.ToUInt16(buffer, offset + 0x28);
            // 2 bytes padding
            m_segmentNumberOnDisk = LittleEndianConverter.ToUInt32(buffer, offset + 0x2C);
            UpdateSequenceNumber = LittleEndianConverter.ToUInt16(buffer, offset + multiSectorHeader.UpdateSequenceArrayOffset);

            if (firstAttributeOffset % 8 > 0)
            {
                throw new InvalidDataException("Invalid file record segment, first attribute is not aligned to 8-byte boundary");
            }

            // Read attributes
            int position = offset + firstAttributeOffset;
            while (!IsEndMarker(buffer, position))
            {
                AttributeRecord attribute = AttributeRecord.FromBytes(buffer, position);

                m_immediateAttributes.Add(attribute);
                position += (int)attribute.RecordLengthOnDisk;
                if (position > offset + segmentLength - 4)
                {
                    throw new InvalidDataException("Invalid file record segment, attribute length exceed segment boundary");
                }
            }

            m_segmentNumber = segmentNumber;
        }

        public byte[] GetBytes(int bytesPerFileRecordSegment, byte minorNTFSVersion, bool applyUsaProtection)
        {
            int strideCount = bytesPerFileRecordSegment / MultiSectorHelper.BytesPerStride;
            ushort updateSequenceArraySize = (ushort)(1 + strideCount);

            ushort updateSequenceArrayOffset;
            if (minorNTFSVersion == 0)
            {
                updateSequenceArrayOffset = NTFS30UpdateSequenceArrayOffset;
            }
            else
            {
                updateSequenceArrayOffset = NTFS31UpdateSequenceArrayOffset;
            }

            MultiSectorHeader multiSectorHeader = new MultiSectorHeader(ValidSignature, updateSequenceArrayOffset, updateSequenceArraySize);
            ushort firstAttributeOffset = GetFirstAttributeOffset(bytesPerFileRecordSegment, minorNTFSVersion);

            int length = applyUsaProtection ? bytesPerFileRecordSegment : GetNumberOfBytesInUse(bytesPerFileRecordSegment, minorNTFSVersion);
            byte[] buffer = new byte[length];
            multiSectorHeader.WriteBytes(buffer, 0x00);
            LittleEndianWriter.WriteUInt64(buffer, 0x08, LogFileSequenceNumber);
            LittleEndianWriter.WriteUInt16(buffer, 0x10, m_sequenceNumber);
            LittleEndianWriter.WriteUInt16(buffer, 0x12, ReferenceCount);
            LittleEndianWriter.WriteUInt16(buffer, 0x14, firstAttributeOffset);
            LittleEndianWriter.WriteUInt16(buffer, 0x16, (ushort)m_flags);

            LittleEndianWriter.WriteInt32(buffer, 0x1C, bytesPerFileRecordSegment);
            m_baseFileRecordSegment.WriteBytes(buffer, 0x20);
            LittleEndianWriter.WriteUInt16(buffer, 0x28, NextAttributeInstance);
            if (minorNTFSVersion == 1)
            {
                LittleEndianWriter.WriteUInt32(buffer, 0x2C, (uint)m_segmentNumber);
            }
            LittleEndianWriter.WriteUInt32(buffer, updateSequenceArrayOffset, UpdateSequenceNumber);

            // write attributes
            int position = firstAttributeOffset;
            foreach (AttributeRecord attribute in m_immediateAttributes)
            {
                byte[] attributeBytes = attribute.GetBytes();
                ByteWriter.WriteBytes(buffer, position, attributeBytes);
                position += attributeBytes.Length;
            }

            LittleEndianWriter.WriteUInt32(buffer, position, AttributesEndMarker);
            position += AttributesEndMarkerLength; // End marker + alignment to 8-byte boundary

            uint segmentLength = (uint)position;
            LittleEndianWriter.WriteUInt32(buffer, 0x18, segmentLength);

            if (applyUsaProtection)
            {
                MultiSectorHelper.ApplyUsaProtection(buffer, 0);
            }
            return buffer;
        }

        public AttributeRecord CreateAttributeRecord(AttributeType type, string name)
        {
            bool isResident = (type != AttributeType.IndexAllocation);
            return CreateAttributeRecord(type, name, isResident);
        }

        internal AttributeRecord CreateAttributeRecord(AttributeType type, string name, bool isResident)
        {
            AttributeRecord attribute = AttributeRecord.Create(type, name, isResident);
            attribute.Instance = NextAttributeInstance;
            NextAttributeInstance++;
            FileRecordHelper.InsertSorted(m_immediateAttributes, attribute);
            return attribute;
        }

        public AttributeRecord CreateAttributeListRecord(bool isResident)
        {
            return CreateAttributeRecord(AttributeType.AttributeList, String.Empty, isResident);
        }

        public AttributeRecord GetImmediateAttributeRecord(AttributeType type, string name)
        {
            foreach (AttributeRecord attribute in m_immediateAttributes)
            {
                if (attribute.AttributeType == type && attribute.Name == name)
                {
                    return attribute;
                }
            }

            return null;
        }

        /// <summary>
        /// This method should only be used to add attributes that have already been sorted
        /// </summary>
        public void AddAttributeRecord(AttributeRecord attribute)
        {
            attribute.Instance = NextAttributeInstance;
            NextAttributeInstance++;
            ImmediateAttributes.Add(attribute);
        }

        public void RemoveAttributeRecord(AttributeType type, string name)
        {
            for (int index = 0; index < m_immediateAttributes.Count; index++)
            {
                if (m_immediateAttributes[index].AttributeType == type && m_immediateAttributes[index].Name == name)
                {
                    m_immediateAttributes.RemoveAt(index);
                    break;
                }
            }
        }

        public int GetNumberOfBytesInUse(int bytesPerFileRecordSegment, byte minorNTFSVersion)
        {
            int length = GetFirstAttributeOffset(bytesPerFileRecordSegment, minorNTFSVersion);
            foreach (AttributeRecord attribute in m_immediateAttributes)
            {
                length += attribute.RecordLength;
            }
            length += AttributesEndMarkerLength; // End marker + alignment to 8-byte boundary
            return length;
        }

        public int GetNumberOfBytesFree(int bytesPerFileRecordSegment, byte minorNTFSVersion)
        {
            int length = GetNumberOfBytesInUse(bytesPerFileRecordSegment, minorNTFSVersion);
            return bytesPerFileRecordSegment - length;
        }

        /// <summary>
        /// Indicates that the file / directory wasn't deleted
        /// </summary>
        public bool IsInUse
        {
            get
            {
                return (m_flags & FileRecordFlags.InUse) != 0;
            }
            set
            {
                if (value)
                {
                    m_flags |= FileRecordFlags.InUse;
                }
                else
                {
                    m_flags &= ~FileRecordFlags.InUse;
                }
            }
        }

        public bool IsDirectory
        {
            get
            {
                return (m_flags & FileRecordFlags.IsDirectory) != 0;
            }
            set
            {
                if (value)
                {
                    m_flags |= FileRecordFlags.IsDirectory;
                }
                else
                {
                    m_flags &= ~FileRecordFlags.IsDirectory;
                }
            }
        }

        public bool IsExtension
        {
            get
            {
                return (m_flags & FileRecordFlags.IsExtension) != 0;
            }
            set
            {
                if (value)
                {
                    m_flags |= FileRecordFlags.IsExtension;
                }
                else
                {
                    m_flags &= ~FileRecordFlags.IsExtension;
                }
            }
        }

        public bool IsSpecialIndex
        {
            get
            {
                return (m_flags & FileRecordFlags.IsSpecialIndex) != 0;
            }
            set
            {
                if (value)
                {
                    m_flags |= FileRecordFlags.IsSpecialIndex;
                }
                else
                {
                    m_flags &= ~FileRecordFlags.IsSpecialIndex;
                }
            }
        }

        public List<AttributeRecord> ImmediateAttributes
        {
            get
            {
                return m_immediateAttributes;
            }
        }

        /// <remarks>
        /// If this is the base file record, the BaseFileRecordSegment value is 0.
        /// https://docs.microsoft.com/en-us/windows/desktop/DevNotes/file-record-segment-header
        /// </remarks>
        public bool IsBaseFileRecord
        {
            get
            {
                return (m_baseFileRecordSegment.SegmentNumber == 0 && m_baseFileRecordSegment.SequenceNumber == 0);
            }
        }

        public bool HasAttributeList
        {
            get
            {
                AttributeRecord attributeList = GetImmediateAttributeRecord(AttributeType.AttributeList, String.Empty);
                return (attributeList != null);
            }
        }

        public long SegmentNumber
        {
            get
            {
                return m_segmentNumber;
            }
        }

        public ushort SequenceNumber
        {
            get
            {
                return m_sequenceNumber;
            }
        }

        public MftSegmentReference SegmentReference
        {
            get
            {
                return new MftSegmentReference(m_segmentNumber, m_sequenceNumber);
            }
        }

        public uint AllocatedLength
        {
            get
            {
                return m_allocatedLength;
            }
        }

        public static bool IsEndMarker(byte[] buffer, int offset)
        {
            uint type = LittleEndianConverter.ToUInt32(buffer, offset + 0x00);
            return (type == AttributesEndMarker);
        }

        public static ushort GetFirstAttributeOffset(int bytesPerFileRecordSegment, byte minorNTFSVersion)
        {
            int strideCount = bytesPerFileRecordSegment / MultiSectorHelper.BytesPerStride;
            ushort updateSequenceArraySize = (ushort)(1 + strideCount);

            ushort updateSequenceArrayOffset;
            if (minorNTFSVersion == 0)
            {
                updateSequenceArrayOffset = NTFS30UpdateSequenceArrayOffset;
            }
            else
            {
                updateSequenceArrayOffset = NTFS31UpdateSequenceArrayOffset;
            }

            // FirstAttributeOffset MUST be aligned to 8-byte boundary
            ushort firstAttributeOffset = (ushort)(Math.Ceiling((double)(updateSequenceArrayOffset + updateSequenceArraySize * 2) / 8) * 8);
            return firstAttributeOffset;
        }

        public static int GetNumberOfBytesAvailable(int bytesPerFileRecordSegment, byte minorNTFSVersion)
        {
            int firstAttributeOffset = FileRecordSegment.GetFirstAttributeOffset(bytesPerFileRecordSegment, minorNTFSVersion);
            return bytesPerFileRecordSegment - firstAttributeOffset - AttributesEndMarkerLength;
        }

        public static bool ContainsFileRecordSegment(byte[] segmentBytes)
        {
            return ContainsFileRecordSegment(segmentBytes, 0);
        }

        public static bool ContainsFileRecordSegment(byte[] segmentBytes, int offset)
        {
            string fileSignature = ByteReader.ReadAnsiString(segmentBytes, offset, 4);
            return (fileSignature == ValidSignature);
        }

        public static ushort GetSequenceNumber(byte[] segmentBytes)
        {
            return GetSequenceNumber(segmentBytes, 0);
        }

        public static ushort GetSequenceNumber(byte[] segmentBytes, int offset)
        {
            return LittleEndianConverter.ToUInt16(segmentBytes, offset + 0x10);
        }

        public static bool ContainsSegmentNumber(List<FileRecordSegment> list, long segmentNumber)
        {
            foreach (FileRecordSegment segment in list)
            {
                if (segment.SegmentNumber == segmentNumber)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
