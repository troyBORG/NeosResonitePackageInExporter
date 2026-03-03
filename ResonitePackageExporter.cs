using BaseX;
using HarmonyLib;
using FrooxEngine;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Threading.Tasks;
using CloudX.Shared;

namespace ResonitePackageExporter
{
    public static class ResonitePackageExporter
    {
        public static Harmony harmony;

        internal static string version = "0.1.6";
        public static bool UseNewtonsoftJson = true;

        static Exception GetInnermostException(Exception ex)
        {
            while (ex is TargetInvocationException tie && tie.InnerException != null)
                ex = tie.InnerException;
            return ex;
        }

        public static void Initialize()
        {
            // Print initialization
            Logger.Log($"Initializing ResonitePackageExplorer v{version}");
            Logger.Log($"Using {typeof(System.Text.Json.JsonSerializer).Assembly.FullName}");
            Logger.Log($"Using {typeof(Newtonsoft.Json.JsonSerializer).Assembly.FullName}");

            harmony = new("Neos.ResonitePackageImporter");
            Logger.Log($"Using {typeof(Harmony).Assembly.FullName}");

            Logger.Log("Patching Methods");

            
            var export = typeof(FileBrowser).GetMethod("CreateNew", BindingFlags.NonPublic | BindingFlags.Instance);
            var exportSetup = typeof(ExportDialog).GetMethod(nameof(ExportDialog.Setup), BindingFlags.Public | BindingFlags.Instance);

            var exportPrefix = typeof(ResonitePackageExporter).GetMethod(nameof(InjectPackageExportable), BindingFlags.Public | BindingFlags.Static);
            var sortExportPatch = typeof(ResonitePackageExporter).GetMethod(nameof(SortExportables), BindingFlags.Public | BindingFlags.Static);

            // When user opens/drops a .resonitepackage file, run our importer instead of spawning as raw file
            var importMethod = typeof(UniversalImporter).GetMethod(nameof(UniversalImporter.Import), BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string), typeof(World), typeof(float3), typeof(floatQ), typeof(bool), typeof(bool) }, null);
            var importPrefix = typeof(ResonitePackageExporter).GetMethod(nameof(UniversalImportResonitePackagePrefix), BindingFlags.Public | BindingFlags.Static);
            if (importMethod != null && importPrefix != null)
                harmony.Patch(importMethod, prefix: new HarmonyMethod(importPrefix));

            // Patch methods
            harmony.Patch(export, prefix: new(exportPrefix));
            harmony.Patch(exportSetup, prefix: new(sortExportPatch));

            Engine.Current.RunPostInit(()=>
            {
                Logger.Log($"ResonitePackageExporter UseNewtonsoftJson: {UseNewtonsoftJson}");

                // CloudXInterface.UseNewtonsoftJson Effectively is Ahead-of-time compilation
                Logger.Log($"INFO: CloudXInterface UseNewtonsoftJson: {CloudXInterface.UseNewtonsoftJson}");
                AddCreateNewActions();
            });
        }

        static void AddCreateNewActions()
        {
            // Add option to switch to using newton soft or system.text.json
            DevCreateNewForm.AddAction("ResonitePackage Tools", "Switch Serialize Method", s =>
            {
                UseNewtonsoftJson = !UseNewtonsoftJson;

                string msg = $"Switched to using: {(UseNewtonsoftJson ? "NewtonsoftJson" : "System.Text.Json")}";

                Logger.Log(msg);

                DevCreateNewForm.SpawnText(s);
                var text = s.GetComponent<TextRenderer>();
                text.Text.Value = msg;

            });
            // Directly export from the create new menu
            DevCreateNewForm.AddAction("ResonitePackage Tools", "Export World", s =>
            {
                s.StartTask(async () =>
                {
                    s.PositionInFrontOfUser(float3.Backward);

                    var fileBrowser = Userspace.Current.World.RootSlot.GetComponentInChildren<FileBrowser>();
                    string path = fileBrowser?.CurrentPath?.Value;

                    if (string.IsNullOrWhiteSpace(path))
                    {
                        var variableResult = await s.Engine.LocalDB.TryReadVariableAsync<string>("FileBrowser.CurrentPath");
                        if (!variableResult.hasValue)
                        {
                            DevCreateNewForm.SpawnText(s);
                            var text = s.GetComponent<TextRenderer>();
                            text.Text.Value = "No file path found, Please select a directory with the File Browser";
                            return;
                        }

                        path = variableResult.value;
                    }

                    var exportDialog = s.AttachComponent<ExportDialog>();

                    var packageExportable = exportDialog.Slot.AttachComponent<PackageExportable>();
                    packageExportable.Root.Target = s.World.RootSlot;

                    exportDialog.Setup(path, [packageExportable]);

                });
            });

            // Import a Resonite package into Neos (object from .resonitepackage file)
            DevCreateNewForm.AddAction("ResonitePackage Tools", "Import Resonite Package", s =>
            {
                s.StartTask(async () =>
                {
                    s.PositionInFrontOfUser(float3.Backward);
                    Slot root = s;

                    string path = null;
                    var pathVar = await s.Engine.LocalDB.TryReadVariableAsync<string>("ResonitePackageExporter.ImportPath");
                    if (pathVar.hasValue && !string.IsNullOrWhiteSpace(pathVar.value))
                        path = pathVar.value;
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        var fileBrowser = Userspace.Current?.World?.RootSlot?.GetComponentInChildren<FileBrowser>();
                        string browserPath = fileBrowser?.CurrentPath?.Value;
                        if (string.IsNullOrWhiteSpace(browserPath))
                        {
                            var v = await s.Engine.LocalDB.TryReadVariableAsync<string>("FileBrowser.CurrentPath");
                            if (v.hasValue) browserPath = v.value;
                        }
                        if (!string.IsNullOrWhiteSpace(browserPath))
                        {
                            try
                            {
                                var files = System.IO.Directory.GetFiles(browserPath, "*.resonitepackage", System.IO.SearchOption.TopDirectoryOnly);
                                if (files != null && files.Length > 0)
                                    path = files[0];
                            }
                            catch (Exception) { }
                        }
                    }

                    if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                    {
                        DevCreateNewForm.SpawnText(s);
                        var text = s.GetComponent<TextRenderer>();
                        text.Text.Value = "No package path. Set variable ResonitePackageExporter.ImportPath to a .resonitepackage file path, or open a folder with a .resonitepackage in File Browser and try again.";
                        return;
                    }

                    DevCreateNewForm.SpawnText(s);
                    var statusText = s.GetComponent<TextRenderer>();
                    s.World.RunSynchronously(() => statusText.Text.Value = "Importing...");

                    try
                    {
                        await PackageImporter.ImportPackageAsync(path, root, (progress, msg) =>
                        {
                            var p = progress;
                            var m = msg;
                            root.World.RunSynchronously(() => statusText.Text.Value = $"{p * 100f:F0}% - {m}");
                        });
                        root.World.RunSynchronously(() => statusText.Text.Value = "Import complete.");
                        Logger.Log("Resonite package imported: " + path);
                    }
                    catch (Exception ex)
                    {
                        var errMsg = ex.Message;
                        root.World.RunSynchronously(() => statusText.Text.Value = "Import failed: " + errMsg);
                        Logger.Error("Import failed: " + ex);
                    }
                });
            });
        }

        /// <summary>
        /// When user opens or drops a .resonitepackage file (File Browser or drag), run our importer instead of spawning as raw file.
        /// </summary>
        public static bool UniversalImportResonitePackagePrefix(string path, World world, float3 position, floatQ rotation, bool silent, bool rawFile, ref Task __result)
        {
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".resonitepackage", StringComparison.OrdinalIgnoreCase))
                return true;
            if (!File.Exists(path))
                return true;

            string name = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(name)) name = "Imported Package";

            __result = world.Coroutines.StartTask(async () =>
            {
                await default(ToWorld);
                Slot slot = world.LocalUserSpace.AddSlot(name, true);
                slot.GlobalPosition = position;
                slot.GlobalRotation = rotation;
                slot.GlobalScale = float3.One;
                try
                {
                    await PackageImporter.ImportPackageAsync(path, slot, null);
                }
                catch (Exception ex)
                {
                    var inner = GetInnermostException(ex);
                    Logger.Error("Import Resonite Package failed (file=" + path + "): " + inner);
                    await default(ToWorld);
                    DevCreateNewForm.SpawnText(slot);
                    var text = slot.GetComponent<TextRenderer>();
                    if (text != null) text.Text.Value = "Import failed: " + inner.Message;
                }
            }, null);
            return false;
        }

        // This is to inject the PackageExportable component before CreateNew method copies Exportables to the Export Dialog
        public static void InjectPackageExportable(FileBrowser __instance, IButton button, ButtonEventData eventData)
        {

            if (!__instance.CanInteract(__instance.LocalUser))
                return;

            Grabber grabber = __instance.World.GetLocalUserGrabberWithItems(eventData.source.Slot);
            
            grabber?.World.RunSynchronously(() =>
                {
                    Slot target = grabber.HolderSlot[0]; // In resonite the export is the first object


                    // Exportable Target is held reference
                    ReferenceProxy componentInChildren = target.GetComponentInChildren<ReferenceProxy>(r => r.Reference.Target is Slot);
                    if (componentInChildren != null)
                        target = (Slot)componentInChildren.Reference.Target;

                    if ((!target.ForeachComponentInChildren<IItemPermissions>(p => p.CanSave) ? 0 : (grabber.World.CanSaveItems() ? 1 : 0)) == 0)
                        return;

                    // Has IExportable
                    List<IExportable> componentsInChildren = target.GetComponentsInChildren<IExportable>();

                    // Add PackageExportable and or ModelExportable components for export menu if they don't exist
                    // Always add a new package exportable if slot is root
                    if (!componentsInChildren.Any(e => e is PackageExportable) || target == target.World.RootSlot)
                    {
                        List<IExportable> cleanup = [];

                        // If no IExportable components exist add a ModelExportable if not root
                        if (componentsInChildren.Count == 0 && target != target.World.RootSlot)
                        {
                            ModelExportable modelExportable = target.AttachComponent<ModelExportable>();
                            modelExportable.Persistent = false;
                            modelExportable.Root.Target = target;
                            cleanup.Add(modelExportable);
                        }
                        
                        // Add PackageExportable component for slot
                        PackageExportable packageExportable = target.AttachComponent<PackageExportable>();
                        packageExportable.Persistent = false;
                        packageExportable.Root.Target = target;
                        cleanup.Add(packageExportable);

                        // Cleanup components after updates
                        // I don't remember if 2 updates is necessary
                        target.RunInUpdates(2, () =>
                        {
                            foreach (var component in cleanup)
                            {
                                component.Destroy();
                            }
                        });
                    }
                });
        }


        /*[HarmonyPrefix]
[HarmonyPatch(typeof(ExportDialog), nameof(ExportDialog.Setup))]*/
        public static void SortExportables(ref IExportable[] exportables)
        {
            // Put package exportables at the front of the list
            var newlist = new List<IExportable>();

            //var rootSlotList = new List<IExportable>();
            foreach (var exportable in exportables)
            {
                if (exportable is PackageExportable /*packageExportable*/)
                {
                   /* // Lazy setup to only include root slot exports
                    if (packageExportable.Root.Target == packageExportable.World.RootSlot)
                    {
                        rootSlotList.Add(exportable);
                    } else
                    {*/
                        newlist.Insert(0, exportable);
                    //}

                    continue;
                }

                newlist.Add(exportable);
            }
            /*
            if (rootSlotList.Count > 0)
            {
                exportables = [.. rootSlotList];
                return;
            }*/

            exportables = [.. newlist];
        }
    }
}
