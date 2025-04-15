/* Copyright (C) 2025-2025 Tal Aloni <tal.aloni.il@gmail.com>. All rights reserved.
 * 
 * You can redistribute this program and/or modify it under the terms of
 * the GNU Lesser Public License as published by the Free Software Foundation,
 * either version 3 of the License, or (at your option) any later version.
 */
using System.IO;
using System.IO.Compression;

namespace DiskAccessLibrary.VMDK
{
    internal static class CompressionHelper
    {
        public static byte[] Decompress(byte[] compressedBytes, int readOffset, int bufferSize)
        {
            if (compressedBytes[readOffset] == 0x78 && compressedBytes[readOffset + 1] == 0x01)
            {
                // Skip zlib header
                readOffset += 2;
            }
            MemoryStream inputStream = new MemoryStream(compressedBytes);
            inputStream.Seek(readOffset, SeekOrigin.Begin);
            DeflateStream deflateStream = new DeflateStream(inputStream, CompressionMode.Decompress);
            byte[] buffer = new byte[bufferSize];
            int writeOffset = 0;
            int bytesRead;
            do
            {
                bytesRead = deflateStream.Read(buffer, writeOffset, buffer.Length - writeOffset);
                writeOffset += bytesRead;
            }
            while (bytesRead > 0 && writeOffset < buffer.Length);

            return buffer;
        }
    }
}
