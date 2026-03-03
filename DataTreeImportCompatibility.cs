using BaseX;
using CodeX;
using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using BrotliSharpLib;

namespace ResonitePackageExporter
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
                throw new InvalidDataException("BSON document truncated.");
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
        /// Load a DataTreeDictionary from a stream in FrDT header + compression + BSON format.
        /// Supports None, LZMA, and Brotli (Resonite package main asset uses Brotli).
        /// </summary>
        public static DataTreeDictionary Load(System.IO.Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            // Ensure we read from the start (zip entry streams may not support Seek; that's ok)
            if (stream.CanSeek && stream.Position != 0)
                stream.Seek(0, SeekOrigin.Begin);

            using var reader = new BinaryReader(stream, Encoding.UTF8, true);

            // FrDT magic
            byte[] magic = reader.ReadBytes(4);
            if (magic.Length < 4 || magic[0] != 'F' || magic[1] != 'r' || magic[2] != 'D' || magic[3] != 'T')
                throw new InvalidDataException("Stream is not FrDT format (invalid magic).");

            // Header: 6-byte (Resonite) = magic(4) + version(2) + compression(1), or 12-byte = magic(4) + version(4) + compression(4)
            // Resonite main asset uses bytes 00 00 03 after magic → compression = 3 (Brotli). Always use the 3rd byte as compression.
            int compression;
            byte[] headerRest = reader.ReadBytes(3); // version (2) + compression (1) for 6-byte, or first 3 of version for 12-byte
            if (headerRest == null || headerRest.Length < 3)
                throw new InvalidDataException("Unexpected end of FrDT header.");
            // Explicit Brotli detection: Resonite packages use 00 00 03 (version 0, compression 3)
            int third = headerRest[2];
            if (headerRest[0] == 0 && headerRest[1] == 0 && headerRest[2] == 3)
                compression = 3; // Brotli
            else if (third >= 0 && third <= 3)
            {
                // 6-byte header: version=headerRest[0,1], compression=headerRest[2]
                compression = third;
            }
            else
            {
                // 12-byte header: we have 3 bytes of version, need 1 more + 4 for compression
                int vLast = reader.ReadByte();
                if (vLast < 0) throw new InvalidDataException("Unexpected end of FrDT header.");
                compression = reader.ReadInt32();
            }

            // Compression enum: None=0, LZ4=1, LZMA=2, Brotli=3 (Resonite packages use Brotli for main asset)
            Logger.Log("FrDT main asset: compression=" + compression + " (0=None,1=LZ4,2=LZMA,3=Brotli)");
            // Read remainder of stream to EOF (zip entry streams often don't report Length)
            byte[] ReadRestOfStream()
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }

            if (compression == (int)DataTreeExportCompatibility.Compression.Brotli)
            {
                byte[] compressed = ReadRestOfStream();
                Logger.Log("FrDT Brotli: compressed bytes=" + compressed.Length);
                if (compressed.Length == 0)
                    throw new InvalidDataException("No Brotli compressed data in stream.");
                using var compressedStream = new MemoryStream(compressed, 0, compressed.Length);
                using var brotliStream = new BrotliStream(compressedStream, CompressionMode.Decompress);
                using var decompressed = new MemoryStream();
                brotliStream.CopyTo(decompressed);
                decompressed.Position = 0;
                Logger.Log("FrDT Brotli: decompressed bytes=" + decompressed.Length);
                return LoadFromDecompressedStream(decompressed);
            }

            if (compression == (int)DataTreeExportCompatibility.Compression.LZMA)
            {
                byte[] compressed = ReadRestOfStream();
                Logger.Log("FrDT LZMA: compressed bytes=" + compressed.Length);
                if (compressed.Length == 0)
                    throw new InvalidDataException("No compressed data in stream.");

                if (Decompress == null)
                    throw new NotSupportedException("LZMA decompression requires BaseX.Helper.Decompress. Cannot load this package.");

                using var compressedStream = new MemoryStream(compressed, 0, compressed.Length);
                using var decompressed = new MemoryStream();
                try
                {
                    Decompress.Invoke(null, [compressedStream, decompressed]);
                }
                catch (TargetInvocationException ex)
                {
                    throw new InvalidOperationException("Helper.Decompress failed. Ensure BaseX is available.", ex.InnerException ?? ex);
                }
                decompressed.Position = 0;
                Logger.Log("FrDT LZMA: decompressed bytes=" + decompressed.Length);
                return LoadFromDecompressedStream(decompressed);
            }

            if (compression == (int)DataTreeExportCompatibility.Compression.None)
            {
                Logger.Log("FrDT None: reading BSON from stream.");
                if (FromRawBSON == null)
                    throw new InvalidOperationException("DataTreeConverter.FromBSON/FromRawBSON not found.");
                // After 6-byte header: BSON document starts with 4-byte size (LE), then (size-4) bytes of content.
                // Pass exactly one document so BsonDataReader doesn't "read past end of container".
                byte[] sizeBuf = reader.ReadBytes(4);
                if (sizeBuf == null || sizeBuf.Length < 4)
                    throw new InvalidDataException("FrDT uncompressed payload too short.");
                const int maxDocSize = 50 * 1024 * 1024;
                int docSize = BitConverter.ToInt32(sizeBuf, 0);
                if (docSize < 4 || docSize > maxDocSize)
                    docSize = (sizeBuf[0] << 24) | (sizeBuf[1] << 16) | (sizeBuf[2] << 8) | sizeBuf[3];
                object root = null;
                if (docSize >= 4 && docSize <= maxDocSize)
                {
                    byte[] doc = new byte[docSize];
                    Array.Copy(sizeBuf, 0, doc, 0, 4);
                    int toRead = docSize - 4;
                    int got = 0;
                    while (got < toRead)
                    {
                        int n = stream.Read(doc, 4 + got, toRead - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    if (got == toRead)
                    {
                        using (var bsonStream = new MemoryStream(doc, 0, docSize, writable: false))
                        {
                            try { root = FromRawBSON.Invoke(null, [bsonStream]); }
                            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
                        }
                    }
                    else if (got > 0)
                        throw new InvalidDataException("FrDT BSON document truncated (got " + (4 + got) + ", expected " + docSize + ").");
                }
                if (root == null)
                {
                    using (var ms = new MemoryStream())
                    {
                        ms.Write(sizeBuf, 0, 4);
                        stream.CopyTo(ms);
                        ms.Position = 0;
                        try { root = FromRawBSON.Invoke(null, [ms]); }
                        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
                    }
                }
                if (root is DataTreeDictionary dict)
                    return dict;
                if (root is DataTreeNode node && node is DataTreeDictionary d)
                    return d;
                throw new InvalidDataException("FromBSON did not return a DataTreeDictionary.");
            }

            throw new NotSupportedException($"Compression type {compression} is not supported for import.");
        }
    }
}
