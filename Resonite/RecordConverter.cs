using System;
using CloudX.Shared;
using Newtonsoft.Json;

namespace NeosResonitePackageInExporter.Resonite
{
    public static class RecordConverter
    {
        // NeosDB Record does currently work fine, however just to be safe if Resonite drops neos record support at some point I'll pre convert to a resonite record here
        public static Record NeosRecordToResonite(CloudX.Shared.Record neosRecord)
        {
            string serializedRecord = JsonConvert.SerializeObject(neosRecord);
            return JsonConvert.DeserializeObject<Record>(serializedRecord);
        }

        /// <summary>
        /// Convert a Resonite (package) record back to Neos CloudX.Shared.Record for LoadObject.
        /// Uses JSON round-trip so we don't depend on Neos RecordVersion/Record shape.
        /// Import path always uses Newtonsoft to avoid System.Text.Json issues in Neos/Wine.
        /// </summary>
        public static CloudX.Shared.Record ResoniteRecordToNeos(Record resoniteRecord)
        {
            if (resoniteRecord == null) return null;
            string json = JsonConvert.SerializeObject(resoniteRecord);
            return JsonConvert.DeserializeObject<CloudX.Shared.Record>(json);
        }
    }
}
