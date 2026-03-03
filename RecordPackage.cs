using CodeX;
using System;
using System.IO;
using System.Text.Json;
using System.IO.Compression;
using System.Threading.Tasks;
using System.Collections.Generic;
using Stream = System.IO.Stream;
using Record = CloudX.Shared.Record;
using CloudX.Shared;
using System.Text;

namespace NeosResonitePackageInExporter
{
    public class RecordPackage : IDisposable
    {
        public const string ASSET_SCHEME = "packdb";
        public const string MAIN_RECORD_ID = "R-Main";
        public const string ASSETS_FOLDER = "Assets";
        public const string VARIANTS_FOLDER = "Variants";
        public const string METADATA_FOLDER = "Metadata";
        public const string RECORD_EXTENSION = ".record";

        private ZipArchive _archive;
        private readonly Dictionary<string, Record> _records = [];
        private readonly Dictionary<string, ZipArchiveEntry> _assets = [];
        private readonly Dictionary<string, Dictionary<string, ZipArchiveEntry>> _variants = [];
        private readonly Dictionary<string, IAssetMetadata> _metadata = [];
        private bool _readMode;

        public static Uri GetAssetURL(string signature) => new("packdb:///" + signature);

        public static string GetAssetSignature(Uri uri)
        {
            if (uri.Scheme != "packdb")
                throw new ArgumentException("Uri is not a package asset URL");
            return uri.Segments.Length < 2 ? null : Path.GetFileNameWithoutExtension(uri.Segments[1]);
        }

        public int RecordCount => _records.Count;

        public int AssetCount => _assets.Count;

        public Record MainRecord
        {
            get
            {
                _records.TryGetValue("R-Main", out Record mainRecord);
                return mainRecord;
            }
        }

        public IEnumerable<Record> Records => _records.Values;

        public IEnumerable<string> Assets => _assets.Keys;
        public IEnumerable<IAssetMetadata> Metadata => _metadata.Values;


        public static RecordPackage Create(Stream writeStream) => new()
        {
            _archive = new ZipArchive(writeStream, ZipArchiveMode.Create)
        };

        /// <summary>
        /// Open an existing package for reading (e.g. to import into Neos).
        /// </summary>
        public static RecordPackage Decode(Stream readStream)
        {
            var pkg = new RecordPackage
            {
                _archive = new ZipArchive(readStream, ZipArchiveMode.Read),
                _readMode = true
            };
            pkg.LoadFromArchive();
            return pkg;
        }

        /// <summary>
        /// Open an existing package file for reading.
        /// </summary>
        public static RecordPackage Decode(string filePath)
        {
            var stream = File.OpenRead(filePath);
            var pkg = new RecordPackage
            {
                _archive = new ZipArchive(stream, ZipArchiveMode.Read),
                _readMode = true
            };
            pkg.LoadFromArchive();
            return pkg;
        }

        private void LoadFromArchive()
        {
            foreach (ZipArchiveEntry entry in _archive.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith(RECORD_EXTENSION, StringComparison.OrdinalIgnoreCase) && !name.Contains("/"))
                {
                    string recordId = Path.GetFileNameWithoutExtension(name);
                    using Stream s = entry.Open();
                    using var sr = new StreamReader(s, Encoding.UTF8);
                    var resoniteRecord = Newtonsoft.Json.JsonConvert.DeserializeObject<Resonite.Record>(sr.ReadToEnd());
                    if (resoniteRecord != null)
                        _records[recordId] = Resonite.RecordConverter.ResoniteRecordToNeos(resoniteRecord);
                }
                else if (name.StartsWith(ASSETS_FOLDER + "/", StringComparison.OrdinalIgnoreCase))
                {
                    string sig = name.Substring((ASSETS_FOLDER + "/").Length).Trim().ToLower();
                    if (!string.IsNullOrEmpty(sig) && !sig.Contains("/"))
                        _assets[sig] = entry;
                }
                else if (name.StartsWith(VARIANTS_FOLDER + "/", StringComparison.OrdinalIgnoreCase))
                {
                    string rest = name.Substring((VARIANTS_FOLDER + "/").Length).Trim();
                    int slash = rest.IndexOf('/');
                    if (slash > 0)
                    {
                        string sig = rest.Substring(0, slash).ToLower();
                        string variantId = rest.Substring(slash + 1);
                        if (!_variants.TryGetValue(sig, out var dict))
                        {
                            dict = new Dictionary<string, ZipArchiveEntry>();
                            _variants[sig] = dict;
                        }
                        dict[variantId] = entry;
                    }
                }
                else if (name.StartsWith(METADATA_FOLDER + "/", StringComparison.OrdinalIgnoreCase))
                {
                    string file = Path.GetFileNameWithoutExtension(name);
                    string ext = Path.GetExtension(name).TrimStart('.');
                    string sig = name.Substring((METADATA_FOLDER + "/").Length);
                    sig = sig.Substring(0, sig.Length - ext.Length - 1).Trim().ToLower();
                    if (string.IsNullOrEmpty(sig)) continue;
                    try
                    {
                        using Stream s = entry.Open();
                        IAssetMetadata meta = DeserializeMetadata(s, ext, sig);
                        if (meta != null)
                            _metadata[sig] = meta;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Could not load metadata {name}: {ex.Message}");
                    }
                }
            }
        }

        private static IAssetMetadata DeserializeMetadata(Stream s, string extension, string assetIdentifier)
        {
            using var sr = new StreamReader(s, Encoding.UTF8);
            string json = sr.ReadToEnd();
            Type t = extension.ToLowerInvariant() switch
            {
                "bitmap" => typeof(BitmapMetadata),
                "cubemap" => typeof(CubemapMetadata),
                "mesh" => typeof(MeshMetadata),
                "shader" => typeof(ShaderMetadata),
                _ => null
            };
            if (t == null) return null;
            var meta = (IAssetMetadata)Newtonsoft.Json.JsonConvert.DeserializeObject(json, t);
            if (meta != null) meta.AssetIdentifier = assetIdentifier;
            return meta;
        }

        /// <summary>
        /// Read a record by id (e.g. "R-Main").
        /// </summary>
        public Record ReadRecord(string recordId)
        {
            _records.TryGetValue(recordId, out var r);
            return r;
        }

        /// <summary>
        /// Open a stream to read an asset by signature. Caller must dispose the stream.
        /// </summary>
        public Stream ReadAsset(string signature)
        {
            if (signature == null) return null;
            signature = signature.ToLower();
            if (!_assets.TryGetValue(signature, out var entry))
                return null;
            return entry.Open();
        }

        /// <summary>
        /// Extract an asset to an output stream.
        /// </summary>
        public void ExtractAsset(string signature, Stream destination)
        {
            using var src = ReadAsset(signature);
            if (src != null)
                src.CopyTo(destination);
        }

        /// <summary>
        /// Extract a variant to an output stream.
        /// </summary>
        public void ExtractVariant(string signature, string variantIdentifier, Stream destination)
        {
            signature = signature.ToLower();
            if (!_variants.TryGetValue(signature, out var dict) || !dict.TryGetValue(variantIdentifier, out var entry))
                return;
            using var src = entry.Open();
            src.CopyTo(destination);
        }

        public bool HasAsset(string signature) => _assets.ContainsKey(signature?.ToLower());

        public bool HasVariant(string signature, string variantIdentifier)
        {
            signature = signature.ToLower();
            return _variants.TryGetValue(signature, out Dictionary<string, ZipArchiveEntry> dictionary) && dictionary.ContainsKey(variantIdentifier);
        }

        public IEnumerable<string> EnumerateVariantsForAsset(string signature)
        {
            signature = signature.ToLower();
            if (_variants.TryGetValue(signature, out Dictionary<string, ZipArchiveEntry> dictionary))
            {
                foreach (KeyValuePair<string, ZipArchiveEntry> keyValuePair in dictionary)
                    yield return keyValuePair.Key;
            }
        }

        public IAssetMetadata TryGetMetadata(string signature)
        {
            return _metadata.TryGetValue(signature, out IAssetMetadata assetMetadata) ? assetMetadata : null;
        }

        public async Task WriteRecord(Record record)
        {
            if (record == null)
                throw new ArgumentNullException(nameof(record));
            if (string.IsNullOrEmpty(record.RecordId))
                throw new ArgumentException("RecordId is empty");
            _records.Add(record.RecordId, record);

            using Stream utf8Json = _archive.CreateEntry(record.RecordId + ".record", CompressionLevel.Optimal).Open();
            // NeosDB Record does currently work fine, however just to be safe if Resonite drops neos record support at some point I'll pre convert to a resonite record here
            await System.Text.Json.JsonSerializer.SerializeAsync(utf8Json, Resonite.RecordConverter.NeosRecordToResonite(record));
        }

        public void WriteMetadata(IAssetMetadata metadata)
        {
            if (string.IsNullOrEmpty(metadata.AssetIdentifier))
                throw new ArgumentException("Metadata is missing asset identifier");
            if (_metadata.ContainsKey(metadata.AssetIdentifier))
                throw new InvalidOperationException("Metadata for asset " + metadata.AssetIdentifier + " has already been added!");
            _metadata.Add(metadata.AssetIdentifier, metadata);
            switch (metadata)
            {
                case BitmapMetadata bitmap:
                    WriteMetadata(bitmap, "bitmap");
                    break;
                case CubemapMetadata cubemap:
                    WriteMetadata(cubemap, "cubemap");
                    break;
                case MeshMetadata mesh:
                    WriteMetadata(mesh, "mesh");
                    break;
                case ShaderMetadata shader:
                    WriteMetadata(shader, "shader");
                    break;
                default:
                    throw new ArgumentException("Unsupported metadata type: " + metadata?.ToString());
            }
        }

        private void WriteMetadata<M>(M metadata, string extension) where M : IAssetMetadata
        {
            try
            {
                using Stream utf8Json = _archive.CreateEntry("Metadata/" + metadata.AssetIdentifier + "." + extension, CompressionLevel.Optimal).Open();

                //JsonSerializer.Serialize(utf8JsonWriter, metadata);
                // CloudXInterface.UseNewtonsoftJson Effectively is Ahead-of-time compilation
                if (NeosResonitePackageInExporter.UseNewtonsoftJson)
                {
                    using StreamWriter streamWriter = new(utf8Json, Encoding.UTF8);
                    streamWriter.Write(Newtonsoft.Json.JsonConvert.SerializeObject(metadata));
                }
                else
                {
                    using Utf8JsonWriter utf8JsonWriter = new(utf8Json, default);
                    System.Text.Json.JsonSerializer.Serialize(utf8JsonWriter, metadata, metadata.GetType(), (System.Text.Json.JsonSerializerOptions)null);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(string.Format("Exception serializing metadata {0}: {1}\n  Maybe try toggling Newtonsoft via the dev create new dialog?\n{2}",
                [
                    metadata.GetType(),
                    metadata.AssetIdentifier,
                    ex
                ]));
                throw;
            }
        }

        public void WriteAsset(string signature, string file)
        {
            using FileStream assetData = File.OpenRead(file);
            WriteAsset(signature, assetData);
        }

        public void WriteAsset(string signature, Stream assetData)
        {
            signature = signature.ToLower();
            Write(_assets, "Assets", signature, assetData);
        }

        public void WriteVariant(string signature, string variantIdentifier, string file)
        {
            using FileStream assetData = File.OpenRead(file);
            WriteVariant(signature, variantIdentifier, assetData);
        }

        public void WriteVariant(string signature, string variantIdentifier, Stream assetData)
        {
            if (!_variants.TryGetValue(signature, out Dictionary<string, ZipArchiveEntry> entries))
            {
                entries = [];
                _variants.Add(signature, entries);
            }
            Write(entries, "Variants/" + signature, variantIdentifier, assetData);
        }

        private void Write(
          Dictionary<string, ZipArchiveEntry> entries,
          string folder,
          string identifier,
          Stream sourceData)
        {
            if (entries.ContainsKey(identifier))
                throw new InvalidOperationException("Asset/Variant " + identifier + " has already been written");
            ZipArchiveEntry entry = _archive.CreateEntry(folder + "/" + identifier, CompressionLevel.NoCompression);
            using (Stream destination = entry.Open())
                sourceData.CopyTo(destination);
            entries.Add(identifier, entry);
        }

        public void Dispose()
        {
            _archive?.Dispose();
            _archive = null;
        }
    }
}
