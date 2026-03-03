using BaseX;
using CodeX;
using FrooxEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CloudX.Shared;

namespace NeosResonitePackageInExporter
{
    /// <summary>
    /// Imports a Resonite package (.resonitepackage) into Neos by extracting assets into LocalDB
    /// and loading the main object into the given slot. Mirrors Resonite's FrooxEngine.PackageImporter logic.
    /// </summary>
    public static class PackageImporter
    {
        /// <summary>
        /// Import a package file into the given slot (as children). Runs on background thread then loads on main thread.
        /// </summary>
        public static async Task ImportPackageAsync(string filePath, Slot root, Action<float, string> progress = null)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException("Package file not found.", filePath);
            if (root == null)
                throw new ArgumentNullException(nameof(root));

            progress?.Invoke(0f, "Decoding package...");
            using var package = RecordPackage.Decode(filePath);
            await ImportPackageAsync(package, root, progress).ConfigureAwait(false);
        }

        /// <summary>
        /// Import an already-decoded package into the given slot.
        /// </summary>
        public static async Task ImportPackageAsync(RecordPackage package, Slot root, Action<float, string> progress = null)
        {
            if (package == null)
                throw new ArgumentNullException(nameof(package));
            if (root == null)
                throw new ArgumentNullException(nameof(root));

            Engine engine = root.Engine;
            await default(ToBackground);

            progress?.Invoke(0.01f, "Decoding object...");
            CloudX.Shared.Record record = package.MainRecord;
            if (record == null)
                throw new NotSupportedException("Package is missing main record (R-Main). Cannot import.");

            if (record.RecordType != "object")
                throw new NotSupportedException("Currently only object packages are supported. RecordType: " + record.RecordType);

            string mainAssetSignature = RecordPackage.GetAssetSignature(new Uri(record.AssetURI));
            if (string.IsNullOrEmpty(mainAssetSignature))
                throw new InvalidDataException("Main record has no asset URI.");

            DataTreeDictionary mainRoot;
            Logger.Log("Import: loading main asset " + mainAssetSignature + " (FrDT+BSON).");
            using (System.IO.Stream mainAssetStream = package.ReadAsset(mainAssetSignature))
            {
                if (mainAssetStream == null)
                    throw new InvalidDataException("Main asset not found in package: " + mainAssetSignature);
                mainRoot = DataTreeImportCompatibility.Load(mainAssetStream);
            }

            if (mainRoot == null)
                throw new InvalidDataException("Failed to load main asset (DataTree).");

            // Convert Resonite/ProtoFlux type names and element names to Neos/LogiX format
            // so that Neos can load the object. Unknown types are left as-is (Neos creates MissingComponent).
            ProtoFluxToLogixConverter.ConvertToLogixFormat(mainRoot);

            var mainGraph = new SavedGraph(mainRoot);
            int totalUrlNodes = mainGraph.URLNodes.Count;
            int processedNodes = 0;
            var assetMapping = new Dictionary<Uri, Uri>();

            foreach (DataTreeValue urlNode in mainGraph.URLNodes)
            {
                processedNodes++;
                if (urlNode.IsNull)
                    continue;

                Uri url = urlNode.TryExtractURL();
                if (url == null || url.Scheme != RecordPackage.ASSET_SCHEME)
                    continue;

                if (assetMapping.TryGetValue(url, out Uri newUrl))
                {
                    urlNode.UpdateValue(newUrl);
                    continue;
                }

                string signature = RecordPackage.GetAssetSignature(url);
                float nodeProgress = (float)processedNodes / Math.Max(1, totalUrlNodes);
                progress?.Invoke(0.01f + 0.98f * nodeProgress, "Importing assets: " + signature);

                // Check if we already have this asset (e.g. by cloud signature)
                AssetRecord existingAsset = await TryFetchAssetByCloudSignatureAsync(engine, signature).ConfigureAwait(false);
                if (existingAsset != null)
                {
                    newUrl = new Uri(existingAsset.url);
                }
                else
                {
                    string tempFile = engine.LocalDB.GetTempFilePath();
                    using (var fstream = File.OpenWrite(tempFile))
                        package.ExtractAsset(signature, fstream);

                    newUrl = await ImportLocalAssetWithSignatureAsync(engine, tempFile, signature).ConfigureAwait(false);
                    if (newUrl == null)
                    {
                        Logger.Warning("ImportLocalAssetWithSignature failed for " + signature + ", using temp file URL.");
                        newUrl = new Uri("file://" + tempFile.Replace("\\", "/"));
                    }

                    // Save metadata from package if engine doesn't have it
                    IAssetMetadata existingMeta = await engine.LocalDB.TryFetchAssetMetadataAsync(newUrl.OriginalString).ConfigureAwait(false);
                    if (existingMeta == null)
                    {
                        IAssetMetadata meta = package.TryGetMetadata(signature);
                        if (meta != null)
                        {
                            meta.AssetIdentifier = newUrl.OriginalString;
                            await SaveAssetMetadataAsync(engine, meta).ConfigureAwait(false);
                            Logger.Log("Imported metadata for " + signature + " from package.");
                        }
                    }

                    // Variants
                    foreach (string variantId in package.EnumerateVariantsForAsset(signature))
                    {
                        Uri variantUrl = new Uri(newUrl, "?" + variantId);
                        AssetRecord existingVariant = await engine.LocalDB.TryFetchAssetRecordAsync(variantUrl).ConfigureAwait(false);
                        if (existingVariant != null)
                            continue;
                        string variantFile = engine.LocalDB.GetTempFilePath();
                        using (var fstream = File.OpenWrite(variantFile))
                            package.ExtractVariant(signature, variantId, fstream);
                        await StoreCacheRecordAsync(engine, variantUrl, variantFile).ConfigureAwait(false);
                        Logger.Log("Imported variant for " + signature + ": " + variantId + " from package.");
                    }
                }

                assetMapping[url] = newUrl;
                urlNode.UpdateValue(newUrl);
            }

            progress?.Invoke(1f, "Loading object...");
            await default(ToWorld);

            LoadObjectIntoSlot(root, mainGraph.Root, record);

            progress?.Invoke(1f, "Imported.");
        }

        static void LoadObjectIntoSlot(Slot root, DataTreeDictionary node, CloudX.Shared.Record record)
        {
            var loadObject = root.GetType().GetMethod("LoadObject",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, null,
                new[] { typeof(DataTreeDictionary), typeof(IRecord), typeof(Slot), typeof(Predicate<Type>), typeof(ReferenceTranslator), typeof(Func<DataTreeNode, DataTreeNode>) }, null);
            if (loadObject != null)
            {
                loadObject.Invoke(root, new object[] { node, record, null, null, null, null });
                return;
            }
            loadObject = root.GetType().GetMethod("LoadObject",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, null,
                new[] { typeof(DataTreeDictionary), typeof(IRecord), typeof(Slot), typeof(Predicate<Type>) }, null);
            if (loadObject != null)
            {
                loadObject.Invoke(root, new object[] { node, record, null, null });
                return;
            }
            loadObject = root.GetType().GetMethod("LoadObject",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, null,
                new[] { typeof(DataTreeDictionary), typeof(IRecord) }, null);
            if (loadObject != null)
            {
                loadObject.Invoke(root, new object[] { node, record });
                return;
            }
            throw new MissingMethodException("Slot.LoadObject(DataTreeDictionary, IRecord, ...) not found.");
        }

        /// <summary>
        /// Try to get LocalDB method to fetch asset by cloud signature (Neos might not have this; Resonite does).
        /// </summary>
        static async Task<AssetRecord> TryFetchAssetByCloudSignatureAsync(Engine engine, string signature)
        {
            try
            {
                var method = engine.LocalDB.GetType().GetMethod("TryFetchAssetByCloudSignatureAsync",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, null,
                    new[] { typeof(string) }, null);
                if (method != null)
                {
                    var task = (Task<AssetRecord>)method.Invoke(engine.LocalDB, new object[] { signature });
                    if (task != null)
                        return await task.ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("TryFetchAssetByCloudSignatureAsync not available or failed: " + ex.Message);
            }
            return null;
        }

        /// <summary>
        /// Import local file into LocalDB, optionally with a cloud signature (Resonite supports this).
        /// </summary>
        static async Task<Uri> ImportLocalAssetWithSignatureAsync(Engine engine, string filePath, string cloudSignature)
        {
            try
            {
                // Resonite: ImportLocalAssetAsync(file, ImportLocation.Move, null, signature)
                var method = engine.LocalDB.GetType().GetMethod("ImportLocalAssetAsync",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, null,
                    new[] { typeof(string), typeof(LocalDB.ImportLocation), typeof(string), typeof(string) }, null);
                if (method != null)
                {
                    var task = (Task<Uri>)method.Invoke(engine.LocalDB, new object[] { filePath, LocalDB.ImportLocation.Move, null, cloudSignature });
                    if (task != null)
                        return await task.ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("ImportLocalAssetAsync with signature not available: " + ex.Message);
            }

            // Fallback: import without signature (old Neos may only have 2 or 3 params)
            try
            {
                var method3 = engine.LocalDB.GetType().GetMethod("ImportLocalAssetAsync",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, null,
                    new[] { typeof(string), typeof(LocalDB.ImportLocation) }, null);
                if (method3 != null)
                {
                    var task = (Task<Uri>)method3.Invoke(engine.LocalDB, new object[] { filePath, LocalDB.ImportLocation.Move });
                    return task != null ? await task.ConfigureAwait(false) : null;
                }
                var method2 = engine.LocalDB.GetType().GetMethod("ImportLocalAssetAsync",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, null,
                    new[] { typeof(string), typeof(LocalDB.ImportLocation), typeof(string) }, null);
                if (method2 != null)
                {
                    var task = (Task<Uri>)method2.Invoke(engine.LocalDB, new object[] { filePath, LocalDB.ImportLocation.Move, null });
                    return task != null ? await task.ConfigureAwait(false) : null;
                }
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error("ImportLocalAssetAsync failed: " + ex.Message);
                return null;
            }
        }

        static async Task SaveAssetMetadataAsync(Engine engine, IAssetMetadata metadata)
        {
            try
            {
                var method = engine.LocalDB.GetType().GetMethod("SaveAssetMetadataAsync",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, null,
                    new[] { typeof(IAssetMetadata) }, null);
                if (method != null)
                {
                    var task = (Task)method.Invoke(engine.LocalDB, new object[] { metadata });
                    if (task != null)
                        await task.ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("SaveAssetMetadataAsync not available or failed: " + ex.Message);
            }
        }

        static async Task StoreCacheRecordAsync(Engine engine, Uri url, string filePath)
        {
            try
            {
                await engine.LocalDB.StoreCacheRecordAsync(url, filePath, false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warning("StoreCacheRecordAsync failed: " + ex.Message);
            }
        }
    }
}
