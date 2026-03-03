using BaseX;
using CodeX;
using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using BrotliSharpLib;

namespace NeosResonitePackageInExporter
{
    /// <summary>
    /// Load DataTree from the same FrDT+LZMA+BSON format used by DataTreeExportCompatibility.
    /// Allows importing Resonite packages (which use this format) into Neos.
    /// </summary>
    public static class DataTreeImportCompatibility
    {
        // Neos/BaseX uses FromBSON(Stream), not FromRawBSON
        static readonly MethodInfo FromRawBSON = typeof(DataTreeConverter).GetMethod("FromBSON", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(System.IO.Stream) }, null)
            ?? typeof(DataTreeConverter).GetMethod("FromRawBSON", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(System.IO.Stream) }, null);
        // BaseX.Helper.Decompress(Stream input, Stream output) or similar; resolve at runtime for old Neos
        static readonly MethodInfo Decompress = GetDecompressMethod();

        static MethodInfo GetDecompressMethod()
        {
            var helperType = Type.GetType("BaseX.Helper, BaseX") ?? Type.GetType("CodeX.Helper, CodeX");
            if (helperType == null) return null;
            return helperType.GetMethod("Decompress", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(System.IO.Stream), typeof(System.IO.Stream) }, null);
        }

        /// <summary>
        /// Parse a decompressed stream that contains a single BSON document (4-byte size LE + content).
        /// Matches Elements.Core / Resonite DataTree load behavior.
        /// </summary>
        static DataTreeDictionary LoadFromDecompressedStream(MemoryStream decompressed)
        {
            if (FromRawBSON == null)
                throw new InvalidOperationException("DataTreeConverter.FromBSON/FromRawBSON not found.");
            decompressed.Position = 0;
            byte[] sizeBuf = new byte[4];
            int sizeRead = decompressed.Read(sizeBuf, 0, 4);
            if (sizeRead < 4)
                throw new InvalidDataException("Decompressed data too short for BSON size.");
            const int maxDocSize = 50 * 1024 * 1024;
            int docSize = BitConverter.ToInt32(sizeBuf, 0);
            if (docSize < 4 || docSize > maxDocSize)
                docSize = (sizeBuf[0] << 24) | (sizeBuf[1] << 16) | (sizeBuf[2] << 8) | sizeBuf[3];
            if (docSize < 4 || docSize > maxDocSize)
                throw new InvalidDataException("BSON document size out of range (LE and BE).");
            Logger.Log("FrDT BSON: document size=" + docSize + ", decompressed stream length=" + decompressed.Length);
            byte[] doc = new byte[docSize];
            Array.Copy(sizeBuf, 0, doc, 0, 4);
            int toRead = docSize - 4;
            int got = 0;
            while (got < toRead)
            {
                int n = decompressed.Read(doc, 4 + got, toRead - got);
                if (n <= 0) break;
                got += n;
            }
            if (got != toRead)
                throw new InvalidDataException("FrDT BSON document truncated (got " + (4 + got) + ", expected " + docSize + ").");
            object root;
            using (var bsonStream = new MemoryStream(doc, 0, docSize, writable: false))
            {
                try { root = FromRawBSON.Invoke(null, [bsonStream]); }
                catch (TargetInvocationException ex)
                {
                    Logger.Error("FrDT BSON parse failed: docSize=" + docSize + ", error=" + (ex.InnerException?.Message ?? ex.Message));
                    throw ex.InnerException ?? ex;
                }
            }
            if (root is DataTreeDictionary dict)
                return dict;
            if (root is DataTreeNode node && node is DataTreeDictionary d)
                return d;
            throw new InvalidDataException("FromBSON did not return a DataTreeDictionary.");
        }

        /// <summary>
        /// Parse header after FrDT magic: support 6-byte (v2+comp1), 9-byte (v4+comp1), 12-byte (v4+comp4).
        /// Returns (compression, payloadStartOffsetInStream). Caller has read 8 bytes after magic into headerRest.
        /// </summary>
        static void ParseHeader(byte[] headerRest, out int compression, out int payloadOffsetFromMagic)
        {
            if (headerRest == null || headerRest.Length < 8)
                throw new InvalidDataException("Unexpected end of FrDT header.");
            // 9-byte header (our export: BinaryWriter.Write(0) = 4 bytes version, then 1 byte enum): magic(4) + version(4) + comp(1)
            if (headerRest[0] == 0 && headerRest[1] == 0 && headerRest[2] == 0 && headerRest[3] == 0 && headerRest[4] >= 0 && headerRest[4] <= 3)
            {
                compression = headerRest[4];
                payloadOffsetFromMagic = 4 + 5; // 9 bytes after magic
                return;
            }
            // 6-byte header: magic(4) + version(2) + comp(1)
            if (headerRest[0] == 0 && headerRest[1] == 0 && headerRest[2] >= 0 && headerRest[2] <= 3)
            {
                compression = headerRest[2];
                payloadOffsetFromMagic = 4 + 3; // 7 bytes after magic
                return;
            }
            // 12-byte header: magic(4) + version(4) + comp(4)
            int comp32 = BitConverter.ToInt32(headerRest, 4);
            if (comp32 >= 0 && comp32 <= 3)
            {
                compression = comp32;
                payloadOffsetFromMagic = 4 + 8; // 12 bytes after magic
                return;
            }
            throw new InvalidDataException("FrDT header compression byte out of range (0-3).");
        }

        /// <summary>
        /// Load a DataTreeDictionary from a stream in FrDT header + compression + BSON format.
        /// Supports None, LZMA, and Brotli (Resonite package main asset uses Brotli).
        /// </summary>
        public static DataTreeDictionary Load(System.IO.Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            if (stream.CanSeek && stream.Position != 0)
                stream.Seek(0, SeekOrigin.Begin);

            byte[] magic = new byte[4];
            int m = stream.Read(magic, 0, 4);
            if (m < 4 || magic[0] != 'F' || magic[1] != 'r' || magic[2] != 'D' || magic[3] != 'T')
                throw new InvalidDataException("Stream is not FrDT format (invalid magic).");

            // Read 8 bytes so we can parse 6/9/12-byte header (payload starts at 7, 9, or 12 after magic)
            byte[] headerRest = new byte[8];
            int hr = 0;
            while (hr < 8)
            {
                int n = stream.Read(headerRest, hr, 8 - hr);
                if (n <= 0) throw new InvalidDataException("Unexpected end of FrDT header.");
                hr += n;
            }
            ParseHeader(headerRest, out int compression, out int payloadOffsetFromMagic);
            int payloadStart = payloadOffsetFromMagic; // bytes from start of stream
            int alreadyConsumed = 4 + 8; // we've read magic + 8
            int payloadInBuffer = Math.Max(0, alreadyConsumed - payloadStart); // bytes of payload we already read in headerRest
            byte[] payloadFromBuffer = payloadInBuffer > 0 ? new byte[payloadInBuffer] : null;
            if (payloadInBuffer > 0)
                Array.Copy(headerRest, 8 - payloadInBuffer, payloadFromBuffer, 0, payloadInBuffer);

            // Read rest of stream and build full payload
            using var restMs = new MemoryStream();
            stream.CopyTo(restMs);
            byte[] rest = restMs.ToArray();
            byte[] payload;
            if (payloadFromBuffer != null && payloadFromBuffer.Length > 0)
            {
                payload = new byte[payloadFromBuffer.Length + rest.Length];
                Array.Copy(payloadFromBuffer, 0, payload, 0, payloadFromBuffer.Length);
                if (rest.Length > 0)
                    Array.Copy(rest, 0, payload, payloadFromBuffer.Length, rest.Length);
            }
            else
                payload = rest;

            Logger.Log("FrDT: compression=" + compression + " (0=None,1=LZ4,2=LZMA,3=Brotli), payload bytes=" + payload.Length);

            if (compression == (int)DataTreeExportCompatibility.Compression.Brotli)
            {
                if (payload.Length == 0)
                    throw new InvalidDataException("No Brotli compressed data in stream.");
                using var compressedStream = new MemoryStream(payload, 0, payload.Length, writable: false);
                using var brotliStream = new BrotliStream(compressedStream, CompressionMode.Decompress);
                using var decompressed = new MemoryStream();
                brotliStream.CopyTo(decompressed);
                decompressed.Position = 0;
                Logger.Log("FrDT Brotli: decompressed bytes=" + decompressed.Length);
                return LoadFromDecompressedStream(decompressed);
            }

            if (compression == (int)DataTreeExportCompatibility.Compression.LZMA)
            {
                if (payload.Length == 0)
                    throw new InvalidDataException("No compressed data in stream.");
                if (Decompress == null)
                    throw new NotSupportedException("LZMA decompression requires BaseX.Helper.Decompress. Cannot load this package.");
                using var compressedStream = new MemoryStream(payload, 0, payload.Length, writable: false);
                using var decompressed = new MemoryStream();
                try { Decompress.Invoke(null, [compressedStream, decompressed]); }
                catch (TargetInvocationException ex)
                { throw new InvalidOperationException("Helper.Decompress failed.", ex.InnerException ?? ex); }
                decompressed.Position = 0;
                Logger.Log("FrDT LZMA: decompressed bytes=" + decompressed.Length);
                return LoadFromDecompressedStream(decompressed);
            }

            if (compression == (int)DataTreeExportCompatibility.Compression.None)
            {
                if (FromRawBSON == null)
                    throw new InvalidOperationException("DataTreeConverter.FromBSON/FromRawBSON not found.");
                return LoadFromDecompressedStream(new MemoryStream(payload, 0, payload.Length, writable: false));
            }

            throw new NotSupportedException($"Compression type {compression} is not supported for import.");
        }
    }
}
