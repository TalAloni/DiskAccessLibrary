/* Copyright (C) 2025-2025 Tal Aloni <tal.aloni.il@gmail.com>. All rights reserved.
 * 
 * You can redistribute this program and/or modify it under the terms of
 * the GNU Lesser Public License as published by the Free Software Foundation,
 * either version 3 of the License, or (at your option) any later version.
 */
using System;
using System.IO;
using System.IO.Compression;
using Utilities;

namespace DiskAccessLibrary.VMDK
{
    /// <remarks>
    /// Note: Virtual Disk Format 1.1 / 5.0 specifications explicitly states that
    /// grain data is compressed with RFC 1951 (DEFLATE).
    /// However, in practice VMware products use RFC 1950 (ZLIB data format), which
    /// is a wrapper around raw deflate compressed data (RFC 1951).
    /// VMware products will verify the Adler-32 checksum in the ZLIB footer, so raw DEFLATE cannot be used.
    /// </remarks>
    public static class ZLibCompressionHelper
    {
        private const byte DeflateCompressionMethod = 0x78;
        private const byte FastestCompressionFlag = 0x01;
        private const byte FastCompressionFlag = 0x5E;
        private const byte DefaultCompressionFlag = 0x9C;
        private const byte MaximumCompressionFlag = 0xDA;

        public static byte[] Decompress(byte[] compressedBytes, int readOffset, int count, int bufferSize)
        {
            return Decompress(compressedBytes, readOffset, count, bufferSize, false);
        }

        public static byte[] Decompress(byte[] compressedBytes, int readOffset, int count, int bufferSize, bool verifyChecksum)
        {
            bool hasZLibHeader = false;
            if (compressedBytes[readOffset] == DeflateCompressionMethod &&
                (compressedBytes[readOffset + 1] == FastestCompressionFlag ||
                compressedBytes[readOffset + 1] == FastCompressionFlag ||
                compressedBytes[readOffset + 1] == DefaultCompressionFlag ||
                 compressedBytes[readOffset + 1] == MaximumCompressionFlag))
            {
                // Skip zlib header
                readOffset += 2;
                hasZLibHeader = true;
            }
            MemoryStream inputStream = new MemoryStream(compressedBytes);
            inputStream.Seek(readOffset, SeekOrigin.Begin);
            byte[] buffer = new byte[bufferSize];
            int writeOffset = 0;
            int bytesRead;
            using (DeflateStream deflateStream = new DeflateStream(inputStream, CompressionMode.Decompress))
            {
                do
                {
                    bytesRead = deflateStream.Read(buffer, writeOffset, buffer.Length - writeOffset);
                    writeOffset += bytesRead;
                }
                while (bytesRead > 0 && writeOffset < buffer.Length);
            }

            if (hasZLibHeader && verifyChecksum)
            {
                uint expectedChecksum = BigEndianConverter.ToUInt32(compressedBytes, readOffset - 2 + count - 4);
                uint checksum = ComputeAdler32Checksum(buffer, 0, buffer.Length);
                if (expectedChecksum != checksum)
                {
                    throw new InvalidDataException("Adler32 checksum of decompressed data does not match stored value");
                }
            }

            return buffer;
        }

        public static byte[] Compress(byte[] data, int offset, int count)
        {
            return Compress(data, offset, count, false);
        }

        public static byte[] Compress(byte[] data, int offset, int count, bool useFastestCompression)
        {
            MemoryStream outputStream = new MemoryStream();
            using (DeflateStream deflateStream = GetDeflateStream(outputStream, useFastestCompression))
            {
                deflateStream.Write(data, offset, count);
            }
            byte[] result = new byte[2 + outputStream.Length + 4];
            result[0] = DeflateCompressionMethod;
            result[1] = useFastestCompression ? FastestCompressionFlag : MaximumCompressionFlag;
            Array.Copy(outputStream.GetBuffer(), 0, result, 2, outputStream.Length);
            uint checksum = ComputeAdler32Checksum(data, offset, count);
            BigEndianWriter.WriteUInt32(result, 2 + (int)outputStream.Length, checksum);

            return result;
        }

        private static DeflateStream GetDeflateStream(Stream outputStream, bool useFastestCompression)
        {
#if NETSTANDARD2_0 || NET472
            return new DeflateStream(outputStream, useFastestCompression ? CompressionLevel.Fastest : CompressionLevel.Optimal, true);
#else
            if (useFastestCompression)
            {
                throw new NotSupportedException("Fastest compression is not supported for .NET Framework 4.0 and below");
            }

            // By default, the compression level is set to Optimal when the compression mode is Compress
            return new DeflateStream(outputStream, CompressionMode.Compress, true);
#endif
        }

        private static uint ComputeAdler32Checksum(byte[] data, int offset, int count)
        {
            // Compute Adler-32:
            ushort a1 = 1, a2 = 0;
            for (int index = offset; index < offset + count; index++)
            {
                byte b = data[index];
                a1 = (ushort)((a1 + b) % 65521);
                a2 = (ushort)((a2 + a1) % 65521);
            }

            return a1 | (uint)a2 << 16;
        }
    }
}
