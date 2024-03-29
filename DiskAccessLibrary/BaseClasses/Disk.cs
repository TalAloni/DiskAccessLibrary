/* Copyright (C) 2014-2018 Tal Aloni <tal.aloni.il@gmail.com>. All rights reserved.
 * 
 * You can redistribute this program and/or modify it under the terms of
 * the GNU Lesser Public License as published by the Free Software Foundation,
 * either version 3 of the License, or (at your option) any later version.
 */

namespace DiskAccessLibrary
{
    public abstract class Disk
    {
        /// <summary>
        /// Sector refers to physical disk sector
        /// </summary>
        public abstract byte[] ReadSectors(long sectorIndex, int sectorCount);
        public abstract void WriteSectors(long sectorIndex, byte[] data);

        public byte[] ReadSector(long sectorIndex)
        {
            return ReadSectors(sectorIndex, 1);
        }

        public abstract int BytesPerSector
        {
            get;
        }

        public abstract long Size
        {
            get;
        }

        public virtual bool IsReadOnly
        {
            get
            {
                return false;
            }
        }

        public long TotalSectors
        {
            get
            {
                return this.Size / this.BytesPerSector;
            }
        }
    }
}

