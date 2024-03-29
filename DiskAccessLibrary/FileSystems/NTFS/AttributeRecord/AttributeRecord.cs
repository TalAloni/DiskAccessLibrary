/* Copyright (C) 2014-2024 Tal Aloni <tal.aloni.il@gmail.com>. All rights reserved.
 * 
 * You can redistribute this program and/or modify it under the terms of
 * the GNU Lesser Public License as published by the Free Software Foundation,
 * either version 3 of the License, or (at your option) any later version.
 */
using System;
using System.IO;
using Utilities;

namespace DiskAccessLibrary.FileSystems.NTFS
{
    /// <summary>
    /// ATTRIBUTE_RECORD_HEADER: https://docs.microsoft.com/en-us/windows/desktop/DevNotes/attribute-record-header
    /// </summary>
    public abstract class AttributeRecord
    {
        public const int MaxAttributeNameLength = 255; // Unicode characters
        internal const int AttributeRecordHeaderLength = 0x10; // The part that is common to both resident and non-resident attributes

        /* Start of ATTRIBUTE_RECORD_HEADER */
        private AttributeType m_attribueType;
        private uint m_recordLengthOnDisk;
        private AttributeForm m_attributeForm;
        private byte m_nameLength; // number of characters
        // ushort NameOffset;
        public AttributeFlags Flags;
        internal ushort Instance;
        /* End of ATTRIBUTE_RECORD_HEADER */
        private string m_name = String.Empty;

        protected AttributeRecord(AttributeType attributeType, string name, bool isResident)
        {
            m_attribueType = attributeType;
            m_name = name;
            m_attributeForm = isResident ? AttributeForm.Resident : AttributeForm.NonResident;
        }

        protected AttributeRecord(byte[] buffer, int offset)
        {
            m_attribueType = (AttributeType)LittleEndianConverter.ToUInt32(buffer, offset + 0x00);
            m_recordLengthOnDisk = LittleEndianConverter.ToUInt32(buffer, offset + 0x04);
            if (m_recordLengthOnDisk < AttributeRecordHeaderLength)
            {
                throw new InvalidDataException("Corrupt attribute, invalid record length");
            }

            m_attributeForm = (AttributeForm)ByteReader.ReadByte(buffer, offset + 0x08);
            m_nameLength = ByteReader.ReadByte(buffer, offset + 0x09);
            ushort nameOffset = LittleEndianConverter.ToUInt16(buffer, offset + 0x0A);
            Flags = (AttributeFlags)LittleEndianConverter.ToUInt16(buffer, offset + 0x0C);
            Instance = LittleEndianConverter.ToUInt16(buffer, offset + 0x0E);
            if (m_nameLength > 0)
            {
                m_name = ByteReader.ReadUTF16String(buffer, offset + nameOffset, m_nameLength);
            }

            if (m_recordLengthOnDisk % 8 > 0)
            {
                throw new InvalidDataException("Corrupt attribute, record not aligned to 8-byte boundary");
            }
        }

        public abstract byte[] GetBytes();

        public void WriteHeader(byte[] buffer, ushort nameOffset)
        {
            m_recordLengthOnDisk = (uint)this.RecordLength;
            m_nameLength = (byte)Name.Length;
            
            ByteWriter.WriteByte(buffer, 0x00, (byte)m_attribueType);
            LittleEndianWriter.WriteUInt32(buffer, 0x04, m_recordLengthOnDisk);
            ByteWriter.WriteByte(buffer, 0x08, (byte)m_attributeForm);
            ByteWriter.WriteByte(buffer, 0x09, m_nameLength);
            LittleEndianWriter.WriteUInt16(buffer, 0x0A, nameOffset);
            LittleEndianWriter.WriteUInt16(buffer, 0x0C, (ushort)Flags);
            LittleEndianWriter.WriteUInt16(buffer, 0x0E, Instance);

            if (m_nameLength > 0)
            {
                ByteWriter.WriteUTF16String(buffer, nameOffset, Name);
            }
        }

        public abstract AttributeRecord Clone();

        public AttributeType AttributeType
        {
            get
            {
                return m_attribueType;
            }
        }

        public bool IsResident
        {
            get
            {
                return (m_attributeForm == AttributeForm.Resident);
            }
        }

        public string Name
        {
            get
            {
                return m_name;
            }
        }

        public abstract ulong DataLength
        {
            get;
        }

        /// <summary>
        /// Each attribute record must be aligned to 8-byte boundary, so RecordLength must be a multiple of 8.
        /// When reading attributes, they may contain additional padding,
        /// so we should use RecordLengthOnDisk to advance the buffer position instead.
        /// </summary>
        public abstract int RecordLength
        {
            get;
        }

        public uint RecordLengthOnDisk
        {
            get
            {
                return m_recordLengthOnDisk;
            }
        }

        public static AttributeRecord Create(AttributeType type, string name, bool isResident)
        {
            if (isResident)
            {
                return ResidentAttributeRecord.Create(type, name);
            }
            else
            {
                return NonResidentAttributeRecord.Create(type, name);
            }
        }

        public static AttributeRecord FromBytes(byte[] buffer, int offset)
        {
            AttributeType attributeType = (AttributeType)LittleEndianConverter.ToUInt32(buffer, offset + 0x00);
            AttributeForm attributeForm = (AttributeForm)ByteReader.ReadByte(buffer, offset + 0x08);
            if (attributeForm == AttributeForm.Resident)
            {
                if (attributeType == AttributeType.StandardInformation)
                {
                    return new StandardInformationRecord(buffer, offset);
                }
                else if (attributeType == AttributeType.FileName)
                {
                    return new FileNameAttributeRecord(buffer, offset);
                }
                else if (attributeType == AttributeType.VolumeName)
                {
                    return new VolumeNameRecord(buffer, offset);
                }
                else if (attributeType == AttributeType.VolumeInformation)
                {
                    return new VolumeInformationRecord(buffer, offset);
                }
                else if (attributeType == AttributeType.IndexRoot)
                {
                    return new IndexRootRecord(buffer, offset);
                }
                else
                {
                    return new ResidentAttributeRecord(buffer, offset);
                }
            }
            else // Non-resident
            {
                if (attributeType == AttributeType.IndexAllocation)
                {
                    return new IndexAllocationRecord(buffer, offset);
                }
                else
                {
                    return new NonResidentAttributeRecord(buffer, offset);
                }
            }
        }
    }
}
