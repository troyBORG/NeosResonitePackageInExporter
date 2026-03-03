using BaseX;
using System;
using System.Collections.Generic;

namespace ResonitePackageExporter
{
    /// <summary>
    /// Converts a DataTree from Resonite (ProtoFlux) format to NeosVR (LogiX) format
    /// so that packages exported from Resonite can be imported in Neos.
    /// Known ProtoFlux types are rewritten to LogiX type names; unknown types are left
    /// as-is so Neos creates MissingComponent (shows as "unknown" and does nothing).
    /// Color space: Resonite uses colorX with ColorProfile (sRGB or Linear); Neos uses
    /// color, typically in linear space. When converting color-type nodes we convert
    /// stored RGB values from sRGB to linear so Neos renders them correctly.
    /// </summary>
    public static class ProtoFluxToLogixConverter
    {
        /// <summary>ProtoFlux (Resonite) type name -> Neos LogiX type name.</summary>
        private static readonly Dictionary<string, string> ProtoFluxToLogixTypeMap = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Element name in ProtoFlux Data -> LogiX Data key (for converted nodes).</summary>
        private static readonly Dictionary<string, string> ElementNameToLogix = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "OnDone", "Next" },
            { "Pulse", "Next" },
            { "Impulse", "Next" },
            { "Inputs", "Operands" },  // only when target expects Operands
            { "DelayTime", "DelaySeconds" },
            { "Delay", "Duration" },
            { "VariableName", "Path" },
            { "OnRead", "OnDone" },
            { "OnWritten", "OnDone" },
            { "OnReadRequest", "OnRequest" },
            { "OnWriteRequest", "OnRequest" },
            { "Tool", "Tooltip" },
        };

        static ProtoFluxToLogixConverter()
        {
            BuildProtoFluxToLogixMap();
        }

        /// <summary>
        /// Convert a DataTree root from Resonite/ProtoFlux format to Neos/LogiX format in-place.
        /// Call this on the main asset DataTree before passing to Slot.LoadObject in Neos.
        /// </summary>
        public static void ConvertToLogixFormat(DataTreeDictionary root)
        {
            if (root == null) return;
            ConvertNode(root);
        }

        private static void ConvertNode(DataTreeNode node)
        {
            if (node == null) return;

            if (node is DataTreeDictionary dict)
            {
                // Worker node: has "Type" and "Data"
                if (dict.TryGetNode("Type") is DataTreeValue typeNode && dict.TryGetNode("Data") != null)
                {
                    string typeName = typeNode.Extract<string>();
                    if (!string.IsNullOrEmpty(typeName) && (IsProtoFluxType(typeName) || IsPhotonDustType(typeName)))
                    {
                        string logixName = TryMapToLogixType(typeName);
                        if (logixName != null)
                        {
                            logixName = ReverseLegacyTypeNameReplacements(logixName);
                            typeNode.UpdateValue(logixName);
                            DataTreeDictionary dataDict = dict.TryGetDictionary("Data");
                            RevertElementNamesInData(dataDict);
                            // Resonite colorX is often sRGB; Neos color is typically linear
                            if (IsColorRelatedType(typeName) || IsColorRelatedType(logixName))
                                ConvertColorValuesInData(dataDict, sRGBToLinear: true);
                        }
                        // else: leave as-is -> Neos will create MissingComponent
                    }
                }

                foreach (var kv in dict.Children)
                    ConvertNode(kv.Value);
            }
            else if (node is DataTreeList list)
            {
                foreach (var child in list.Children)
                    ConvertNode(child);
            }
        }

        private static bool IsProtoFluxType(string typeName)
        {
            return typeName.IndexOf("ProtoFlux", StringComparison.OrdinalIgnoreCase) >= 0
                   || typeName.IndexOf("FrooxEngine.ProtoFlux", StringComparison.Ordinal) >= 0;
        }

        private static bool IsPhotonDustType(string typeName)
        {
            return typeName.IndexOf("PhotonDust", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Reverse the legacy type name replacements Resonite applies when loading Neos content,
        /// so we output the original Neos type names (RoundToInt not LegacyRoundToInt, etc.).
        /// </summary>
        private static string ReverseLegacyTypeNameReplacements(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return typeName;
            return typeName
                .Replace("LegacyRoundToInt", "RoundToInt")
                .Replace("LegacyCeilToInt", "CeilToInt")
                .Replace("LegacyFloorToInt", "FloorToInt")
                .Replace("Legacy_Cast", "Cast")
                .Replace("IsUserPresentInWorld", "IsUserPresent");
        }

        private static string TryMapToLogixType(string protoFluxTypeName)
        {
            // Normalize: strip assembly info if present (e.g. ", ProtoFlux.Runtimes.Execution")
            string key = protoFluxTypeName;
            int comma = key.IndexOf(',');
            if (comma > 0)
                key = key.Substring(0, comma).Trim();

            if (ProtoFluxToLogixTypeMap.TryGetValue(key, out string logix))
                return logix;

            // Try without generic args for generic types (e.g. ValueAdd`1[[System.Single]] -> try ValueAdd_Float)
            int backtick = key.IndexOf('`');
            if (backtick > 0)
            {
                string baseName = key.Substring(0, backtick);
                string genericPart = key.Substring(backtick);
                string shortKey = baseName + genericPart;
                if (ProtoFluxToLogixTypeMap.TryGetValue(shortKey, out logix))
                    return logix;
            }

            return null;
        }

        private static bool IsColorRelatedType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            return typeName.IndexOf("colorX", StringComparison.OrdinalIgnoreCase) >= 0
                   || typeName.IndexOf(".Color", StringComparison.Ordinal) >= 0
                   || typeName.IndexOf("ColorInput", StringComparison.Ordinal) >= 0
                   || typeName.IndexOf("ColorSource", StringComparison.Ordinal) >= 0
                   || typeName.IndexOf("Elements.Core.color", StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// Convert color values in a worker's Data dict between sRGB and linear.
        /// Resonite colorX often stores sRGB; Neos color is typically linear.
        /// </summary>
        private static void ConvertColorValuesInData(DataTreeDictionary data, bool sRGBToLinear)
        {
            if (data == null) return;
            try
            {
                // Common layout: R, G, B, A as separate nodes (Sync fields)
                if (data.Children.TryGetValue("R", out DataTreeNode rNode) &&
                    data.Children.TryGetValue("G", out DataTreeNode gNode) &&
                    data.Children.TryGetValue("B", out DataTreeNode bNode))
                {
                    if (rNode is DataTreeValue rv && gNode is DataTreeValue gv && bNode is DataTreeValue bv)
                    {
                        float r = rv.Extract<float>(), g = gv.Extract<float>(), b = bv.Extract<float>();
                        if (sRGBToLinear)
                            SRGBToLinear(ref r, ref g, ref b);
                        else
                            LinearToSRGB(ref r, ref g, ref b);
                        rv.UpdateValue(r);
                        gv.UpdateValue(g);
                        bv.UpdateValue(b);
                    }
                }
                // Single "Value" that might be a color (e.g. 4 floats in a list or packed)
                if (data.Children.TryGetValue("Value", out DataTreeNode valueNode) && valueNode is DataTreeDictionary valueDict)
                {
                    if (valueDict.Children.TryGetValue("R", out DataTreeNode vr) &&
                        valueDict.Children.TryGetValue("G", out DataTreeNode vg) &&
                        valueDict.Children.TryGetValue("B", out DataTreeNode vb))
                    {
                        if (vr is DataTreeValue vrv && vg is DataTreeValue vgv && vb is DataTreeValue vbv)
                        {
                            float r = vrv.Extract<float>(), g = vgv.Extract<float>(), b = vbv.Extract<float>();
                            if (sRGBToLinear)
                                SRGBToLinear(ref r, ref g, ref b);
                            else
                                LinearToSRGB(ref r, ref g, ref b);
                            vrv.UpdateValue(r);
                            vgv.UpdateValue(g);
                            vbv.UpdateValue(b);
                        }
                    }
                }
            }
            catch
            {
                // Ignore if layout doesn't match or Extract/UpdateValue fails
            }
        }

        private static void SRGBToLinear(ref float r, ref float g, ref float b)
        {
            r = SRGBChannelToLinear(r);
            g = SRGBChannelToLinear(g);
            b = SRGBChannelToLinear(b);
        }

        private static void LinearToSRGB(ref float r, ref float g, ref float b)
        {
            r = LinearChannelToSRGB(r);
            g = LinearChannelToSRGB(g);
            b = LinearChannelToSRGB(b);
        }

        private static float SRGBChannelToLinear(float c)
        {
            if (c <= 0.04045f) return c / 12.92f;
            return (float)Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        private static float LinearChannelToSRGB(float c)
        {
            if (c <= 0.0031308f) return c * 12.92f;
            return (float)(1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055);
        }

        private static void RevertElementNamesInData(DataTreeDictionary data)
        {
            if (data == null) return;
            try
            {
                // Only revert keys that are purely ProtoFlux names and where we have a LogiX equivalent.
                var renames = new List<(string from, string to)>();
                foreach (var kv in data.Children)
                {
                    if (ElementNameToLogix.TryGetValue(kv.Key, out string logixName) && logixName != kv.Key)
                    {
                        if (!data.Children.ContainsKey(logixName))
                            renames.Add((kv.Key, logixName));
                    }
                }
                foreach (var (from, to) in renames)
                {
                    if (data.Children.TryGetValue(from, out DataTreeNode val))
                    {
                        data.Children.Remove(from);
                        data.Children[to] = val;
                    }
                }
            }
            catch
            {
                // If Children is read-only or API differs, skip element reversion; type conversion still applies.
            }
        }

        private static void BuildProtoFluxToLogixMap()
        {
            // Map: Resonite/ProtoFlux full type name (no assembly) -> Neos LogiX full type name.
            // Built from Resonite LegacyProtoFluxLoader + ProtoFluxMapper (forward) inverted for Neos LogiX.
            // C# uses System.Single = float, System.Double = double, System.Int32 = int, etc.

            string P = "ProtoFlux.Runtimes.Execution.Nodes";
            string POp = P + ".Operators";
            string PCore = "FrooxEngine.ProtoFlux.CoreNodes";
            string L = "FrooxEngine.LogiX";
            string LOp = L + ".Operators";
            string LIn = L + ".Input";
            string LCast = L + ".Cast";

            // --- Value sources (inputs) - Neos: FrooxEngine.LogiX.Input.FloatInput etc. ---
            Add(PCore + ".ValueSource`1[[System.Single]]", LIn + ".FloatInput");
            Add(PCore + ".ValueSource`1[[System.Double]]", LIn + ".DoubleInput");
            Add(PCore + ".ValueSource`1[[System.Boolean]]", LIn + ".BoolInput");
            Add(PCore + ".ValueSource`1[[System.Int32]]", LIn + ".IntInput");
            Add(PCore + ".ValueSource`1[[System.String]]", LIn + ".StringInput");
            Add(PCore + ".ValueSource`1[[System.Int64]]", LIn + ".LongInput");
            Add(PCore + ".ValueSource`1[[Elements.Core.float2]]", LIn + ".Float2Input");
            Add(PCore + ".ValueSource`1[[Elements.Core.float3]]", LIn + ".Float3Input");
            Add(PCore + ".ValueSource`1[[Elements.Core.colorX]]", LIn + ".ColorInput");
            Add(PCore + ".ValueSource`1[[Elements.Core.quaternion]]", LIn + ".QuaternionInput");

            // --- Operators: Add, Sub, Mul, Div (float and common) ---
            Add(POp + ".ValueAdd`1[[System.Single]]", LOp + ".Add_Float_Float");
            Add(POp + ".ValueSub`1[[System.Single]]", LOp + ".Sub_Float_Float");
            Add(POp + ".ValueMul`1[[System.Single]]", LOp + ".Mul_Float_Float");
            Add(POp + ".ValueDiv`1[[System.Single]]", LOp + ".Div_Float_Float");
            Add(POp + ".ValueAdd`1[[System.Double]]", LOp + ".Add_Double_Double");
            Add(POp + ".ValueSub`1[[System.Double]]", LOp + ".Sub_Double_Double");
            Add(POp + ".ValueAdd`1[[System.Int32]]", LOp + ".Add_Int_Int");
            Add(POp + ".ValueSub`1[[System.Int32]]", LOp + ".Sub_Int_Int");
            Add(POp + ".ValueAdd`1[[Elements.Core.colorX]]", LOp + ".Add_ColorX");
            Add(POp + ".ValueAdd`1[[Elements.Core.float2]]", LOp + ".Add_Float2_Float2");
            Add(POp + ".ValueAdd`1[[Elements.Core.float3]]", LOp + ".Add_Float3_Float3");

            // --- Comparisons (Neos uses SmallerOrEqual, not LessOrEqual) ---
            Add(POp + ".ValueLessThan`1[[System.Single]]", LOp + ".LessThan_Float");
            Add(POp + ".ValueLessThan`1[[System.Double]]", LOp + ".LessThan_Double");
            Add(POp + ".ValueLessThan`1[[System.Int32]]", LOp + ".LessThan_Int");
            Add(POp + ".ValueLessOrEqual`1[[System.Single]]", LOp + ".SmallerOrEqual_Float");
            Add(POp + ".ValueLessOrEqual`1[[System.Double]]", LOp + ".SmallerOrEqual_Double");
            Add(POp + ".ValueLessOrEqual`1[[System.Int32]]", LOp + ".SmallerOrEqual_Int");
            Add(POp + ".ValueGreaterThan`1[[System.Single]]", LOp + ".GreaterThan_Float");
            Add(POp + ".ValueGreaterThan`1[[System.Int32]]", LOp + ".GreaterThan_Int");
            Add(POp + ".ValueGreaterOrEqual`1[[System.Single]]", LOp + ".GreaterOrEqual_Float");
            Add(POp + ".ValueGreaterOrEqual`1[[System.Int32]]", LOp + ".GreaterOrEqual_Int");
            Add(POp + ".ValueEquals`1[[System.Single]]", LOp + ".Equals_Float");
            Add(POp + ".ValueEquals`1[[System.Boolean]]", LOp + ".Equals_Bool");
            Add(POp + ".ValueEquals`1[[System.Int32]]", LOp + ".Equals_Int");
            Add(POp + ".ValueNotEquals`1[[System.Single]]", LOp + ".NotEquals_Float");
            Add(POp + ".ValueNotEquals`1[[System.Boolean]]", LOp + ".NotEquals_Bool");

            // --- Conditional ---
            Add(POp + ".ValueConditional`1[[System.Single]]", LOp + ".Conditional_Float");
            Add(POp + ".ValueConditional`1[[System.Boolean]]", LOp + ".Conditional_Bool");
            Add(POp + ".ValueConditional`1[[System.Int32]]", LOp + ".Conditional_Int");

            // --- Math ---
            Add(POp + ".ValueNegate`1[[System.Single]]", LOp + ".Negate_Float");
            Add(POp + ".ValueSquare`1[[System.Single]]", LOp + ".Square_Float");
            Add(POp + ".ValueMin`1[[System.Single]]", LOp + ".Min_Float");
            Add(POp + ".ValueMax`1[[System.Single]]", LOp + ".Max_Float");
            Add(POp + ".ValueClamp`1[[System.Single]]", LOp + ".Clamp_Float");
            Add(POp + ".ValueLerp`1[[System.Single]]", LOp + ".Lerp_Float");
            Add(POp + ".ValueAbs`1[[System.Single]]", LOp + ".Abs_Float");
            Add(POp + ".ValueMod`1[[System.Single]]", LOp + ".Mod_Float");
            Add(POp + ".ValueMod`1[[System.Int32]]", LOp + ".Mod_Int");
            Add(POp + ".ValueOneMinus`1[[System.Single]]", LOp + ".OneMinus_Float");
            Add(POp + ".ValueReciprocal`1[[System.Single]]", LOp + ".Reciprocal_Float");
            Add(POp + ".ValueInc`1[[System.Single]]", LOp + ".Inc_Float");
            Add(POp + ".ValueDec`1[[System.Single]]", LOp + ".Dec_Float");

            // --- Relays - Neos: FrooxEngine.LogiX.RelayNode`1[[System.Single]] etc. ---
            Add(POp + ".ValueRelay`1[[System.Single]]", L + ".RelayNode`1[[System.Single]]");
            Add(POp + ".ValueRelay`1[[System.Boolean]]", L + ".RelayNode`1[[System.Boolean]]");
            Add(POp + ".ValueRelay`1[[System.Int32]]", L + ".RelayNode`1[[System.Int32]]");
            Add(POp + ".ValueRelay`1[[System.String]]", L + ".RelayNode`1[[System.String]]");
            Add(POp + ".ValueRelay`1[[Elements.Core.float3]]", L + ".RelayNode`1[[Elements.Core.float3]]");
            Add(POp + ".ValueRelay`1[[Elements.Core.colorX]]", L + ".RelayNode`1[[Elements.Core.colorX]]");

            // --- Time - Neos: FrooxEngine.LogiX.Input.DeltaTimeNode etc. ---
            Add(P + ".TimeAndDate.DeltaTime", LIn + ".DeltaTimeNode");
            Add(P + ".TimeAndDate.Time", LIn + ".TimeNode");
            Add(P + ".TimeAndDate.UtcNow", LIn + ".UtcNowNode");

            // --- Impulses / flow - Neos LogiX has ImpulseRelay in Operators ---
            Add("FrooxEngine.ProtoFlux.CoreNodes.ContinuationRelay", LOp + ".ImpulseRelay");
            Add(P + ".ContinuationRelay", LOp + ".ImpulseRelay");

            // --- Casts (common) ---
            Add(POp + ".Casts.Cast_Float_To_Int", LCast + ".Cast_float_To_int");
            Add(POp + ".Casts.Cast_Int_To_Float", LCast + ".Cast_int_To_float");
            Add(POp + ".Casts.Cast_Double_To_Float", LCast + ".Cast_double_To_float");
            Add(POp + ".Casts.Cast_Float_To_Double", LCast + ".Cast_float_To_double");
            Add(POp + ".Casts.Cast_Int_To_Double", LCast + ".Cast_int_To_double");
            Add(POp + ".Casts.Cast_Double_To_Int", LCast + ".Cast_double_To_int");
            Add(POp + ".Casts.Cast_Bool_To_Float", LCast + ".Cast_bool_To_float");
            Add(POp + ".Casts.Cast_Float_To_Bool", LCast + ".Cast_float_To_bool");

            // --- Drivers / write - Neos: FrooxEngine.LogiX.DriverNode`1[[System.Single]] etc. ---
            Add(PCore + ".ValueFieldDrive`1[[System.Single]]", L + ".DriverNode`1[[System.Single]]");
            Add(PCore + ".ValueFieldDrive`1[[System.Boolean]]", L + ".DriverNode`1[[System.Boolean]]");
            Add(PCore + ".ValueFieldDrive`1[[Elements.Core.float3]]", L + ".DriverNode`1[[Elements.Core.float3]]");
            Add(PCore + ".ValueFieldDrive`1[[Elements.Core.quaternion]]", L + ".DriverNode`1[[Elements.Core.quaternion]]");
            Add(PCore + ".ValueFieldDrive`1[[Elements.Core.colorX]]", L + ".DriverNode`1[[Elements.Core.colorX]]");

            // --- Particle system (Resonite PhotonDust -> Neos ParticleSystem/ParticleStyle/Emitters) ---
            // LegacyPhotonDustLoader maps FrooxEngine.* -> FrooxEngine.PhotonDust.* when loading in Resonite.
            // Reverse: PhotonDust types -> Neos FrooxEngine types. PhotonDust-only modules stay unmapped (MissingComponent).
            string PDust = "FrooxEngine.PhotonDust";
            Add(PDust + ".ParticleSystem", "FrooxEngine.ParticleSystem");
            Add(PDust + ".ParticleStyle", "FrooxEngine.ParticleStyle");
            Add(PDust + ".PointEmitter", "FrooxEngine.PointEmitter");
            Add(PDust + ".BoxEmitter", "FrooxEngine.BoxEmitter");
            Add(PDust + ".SphereEmitter", "FrooxEngine.SphereEmitter");
            Add(PDust + ".CircleEmitter", "FrooxEngine.CircleEmitter");
            Add(PDust + ".ConeEmitter", "FrooxEngine.ConeEmitter");
            Add(PDust + ".CylinderEmitter", "FrooxEngine.CylinderEmitter");

            // Additional mappings can be added here as needed; unmapped ProtoFlux/PhotonDust types
            // will remain as-is and Neos will create MissingComponent for them.
        }

        private static void Add(string protoFluxKey, string logixName)
        {
            if (!string.IsNullOrEmpty(protoFluxKey) && !string.IsNullOrEmpty(logixName))
                ProtoFluxToLogixTypeMap[protoFluxKey] = logixName;
        }
    }
}
