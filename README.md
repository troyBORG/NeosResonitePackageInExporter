# ResonitePackageExporter

A plugin for [Neos VR](https://neos.com/) that adds Resonite package **export** and **import**.

- **Export** — Save objects or worlds from Neos as `.resonitepackage` files (for use in Resonite).
- **Import** — Load a `.resonitepackage` file (from Resonite or this exporter) back into Neos. The mod converts Resonite/ProtoFlux and PhotonDust data back to Neos/LogiX and legacy particle types so content loads correctly.

You can import worlds into Resonite using the [ImportWorldResonitePackage mod](https://github.com/badhaloninja/ImportWorldResonitePackage).

**Experimental:** Round-tripping between Neos and Resonite may cause data loss.

---

## Quick start

<details>
<summary><strong>Build</strong></summary>

- **.NET Framework 4.7.2** — The project uses `Microsoft.NETFramework.ReferenceAssemblies.net472` from NuGet, so you can build on Linux/macOS without the Windows Developer Pack.
- **Neos assemblies** — Build needs FrooxEngine, BaseX, CodeX, CloudX.Shared, Harmony, etc.

| Option | What to do |
|--------|------------|
| **Neos in a standard Steam path** | No extra steps; the project finds it (Windows/Linux Steam paths are checked). |
| **Neos archive / custom path** | Edit the `.csproj` to point at your path (e.g. `/mnt/4tb/Neos Archive/app`). The built DLL is copied into that install’s `Libraries/` after each successful build. |
| **No Neos install** | Copy `Neos_Data/Managed` and `Libraries/0Harmony.dll` into the repo’s `NeosRefs/` folder (see `NeosRefs/README.md`), then `dotnet build`. |

From the repo root: **`dotnet build`**. On Windows with Neos in a standard path, the PostBuild script may copy the DLL into Neos `Libraries`. On Linux, PostBuild is skipped; when using the Neos archive path, the DLL is copied to `Libraries` instead.

</details>

<details>
<summary><strong>Install</strong></summary>

1. Put **ResonitePackageExporter.dll** and **0Harmony.dll** in Neos **`Libraries/`**.
2. Put **BrotliSharpLib.dll** in **`Libraries/`** (required for import; build copies it automatically).
3. **Do not** put `ResonitePackageExporter.pdb` in `Libraries/` (can cause PostX symbol errors).
4. Launch Neos with the mod loaded (see below). Harmony is loaded by the mod; you don’t need to load it yourself.

**Launch arguments** (Steam or NeosLauncher). Load NeosModLoader first, then this mod:

```text
-LoadAssembly Libraries/NeosModLoader.dll -LoadAssembly Libraries/ResonitePackageExporter.dll
```

- **Steam:** Right‑click Neos VR → Properties → Launch Options → paste the line.
- **NeosLauncher:** Use its launch-arguments field.
- **This mod only (no NML):** `-LoadAssembly Libraries/ResonitePackageExporter.dll`.

</details>

<details>
<summary><strong>Usage — Export (Neos → Resonite package)</strong></summary>

- When exporting an object/slot via the files tab, a **ResonitePackage** option appears at the top of the export dialog. You can export with or without variants.
- To export a world: hold a slot reference to the Root slot and choose **World Resonite Package**, or use **DevCreateNew → ResonitePackage Tools → Export World**.
- The plugin adds a `PackageExportable` component that can be used like any other `Exportable`.

</details>

<details>
<summary><strong>Usage — Import (Resonite package → Neos)</strong></summary>

- **Drag & drop or File Browser:** Open or drop a **`.resonitepackage`** file. The mod intercepts it and runs the package importer, spawning the object at the drop/open location.
- **DevCreateNew → ResonitePackage Tools → Import Resonite Package:** Imports using variable `ResonitePackageExporter.ImportPath` or the first `.resonitepackage` in the File Browser’s current folder.
- Only **object** packages are supported (not full worlds), matching Resonite’s package importer.

**Avatars with SimpleAvatarProtection (Resonite):** If the package was exported from Resonite with [SimpleAvatarProtection](https://wiki.resonite.com/Component:SimpleAvatarProtection) enabled, Neos has no equivalent to Resonite’s “reassign user on package import.” After import you may need to **manually assign or equip** the avatar; the **User** ref and in-tree owner refs may be wrong. Import still succeeds.

</details>

---

## Compatibility & conversion (import into Neos)

When you import a `.resonitepackage` that was created in **Resonite**, the main asset is stored in Resonite’s format (ProtoFlux nodes, PhotonDust particle system, etc.). Neos expects LogiX and the old particle types. The mod converts the DataTree **before** Neos loads it so that content loads as much as possible.

<details>
<summary><strong>ProtoFlux → LogiX (nodes / logic)</strong></summary>

- **Resonite** uses **ProtoFlux** (e.g. `ProtoFlux.Runtimes.Execution.Nodes.Operators.ValueAdd`, `FrooxEngine.ProtoFlux.CoreNodes.ValueSource`).
- **Neos** uses **LogiX** (e.g. `FrooxEngine.LogiX.Operators.Add_Float_Float`, `FrooxEngine.LogiX.Input.FloatInput`).
- The converter rewrites worker **type names** in the DataTree from ProtoFlux to the matching LogiX type when a mapping exists.
- **Element names** are reverted where Resonite renamed them (e.g. `OnDone` → `Next`, `Inputs` → `Operands`, `DelayTime` → `DelaySeconds`).
- **Unknown ProtoFlux types** (no mapping) are left as-is; Neos creates a **MissingComponent** (shows as unsupported type, does nothing).
- Mapped areas include: value sources (FloatInput, BoolInput, etc.), operators (Add, Sub, Mul, Div, comparisons, conditional, math), relays, time nodes, casts, drivers, and more.

</details>

<details>
<summary><strong>Color space (sRGB → linear)</strong></summary>

- **Resonite** uses **colorX** with a **ColorProfile** (sRGB or Linear).
- **Neos** uses **color** and typically expects **linear** for rendering.
- For worker types that are color-related, the converter transforms stored **R, G, B** values in the Data from **sRGB to linear** so colors look correct in Neos.

</details>

<details>
<summary><strong>Particle system (PhotonDust → Neos)</strong></summary>

- **Resonite** uses **PhotonDust** (`FrooxEngine.PhotonDust.ParticleSystem`, `ParticleStyle`, and emitters).
- **Neos** uses the legacy types (`FrooxEngine.ParticleSystem`, `FrooxEngine.ParticleStyle`, and the same emitter names).
- The converter maps: **ParticleSystem**, **ParticleStyle**, and emitters (**PointEmitter**, **BoxEmitter**, **SphereEmitter**, **CircleEmitter**, **ConeEmitter**, **CylinderEmitter**) from PhotonDust back to Neos.
- **PhotonDust-only modules** (e.g. LifetimeRangeInitializer, BillboardParticleRenderer) have no Neos equivalent; they are left unmapped and become **MissingComponent**. Core system/style/emitters load as Neos components.
- Data layout differences between PhotonDust and Neos may mean some particle fields don’t line up; type mapping ensures the right component type loads.

</details>

<details>
<summary><strong>Legacy type name cleanup</strong></summary>

When Resonite loads old Neos content it rewrites some type names (e.g. `RoundToInt` → `LegacyRoundToInt`). When we output type names for Neos we reverse these so Neos sees the original names:

- `LegacyRoundToInt` → `RoundToInt`
- `LegacyCeilToInt` → `CeilToInt`
- `LegacyFloorToInt` → `FloorToInt`
- `Legacy_Cast` → `Cast`
- `IsUserPresentInWorld` → `IsUserPresent`

</details>

---

## Credits

- PostX script from [XDelta's NeosFileStreamWriter](https://github.com/XDelta/NeosFileStreamWriter/).
