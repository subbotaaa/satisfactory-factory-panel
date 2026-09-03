using System;
using System.IO;
using System.IO.Compression;
using BigAmbitions.SaveSystem.Legacy;
using Entities;
using OdinSerializer;

namespace BigAmbitionsMonitor
{
    /// <summary>
    /// Десериализует файл сохранения Big Ambitions (.hsg = GZip поверх Odin binary)
    /// в GameInstance — повторяя пайплайн игры (SaveGameSerializationHelper.DeserializeBinaryData),
    /// но со своим логгером, чтобы не тянуть UnityEngine.Debug.
    /// </summary>
    public static class SaveDeserializer
    {
        private sealed class SilentLogger : OdinSerializer.ILogger
        {
            public void LogWarning(string w) { }
            public void LogError(string e) { }
            public void LogException(Exception ex) { }
        }

        public static GameInstance Load(string hsgPath)
        {
            byte[] raw;
            using (var fs = File.OpenRead(hsgPath))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var ms = new MemoryStream())
            {
                gz.CopyTo(ms);
                raw = ms.ToArray();
            }

            var ctx = new DeserializationContext();
            ctx.Config.DebugContext.Logger = new SilentLogger();
            ctx.Config.DebugContext.LoggingPolicy = LoggingPolicy.LogErrors;
            ctx.Config.DebugContext.ErrorHandlingPolicy = ErrorHandlingPolicy.Resilient;

            using var body = new MemoryStream(raw);
            using var inner = new BinaryDataReader(body, ctx);
            using var reader = new IntToStringMigratingDataReader(inner,
                LegacyHelper.GetMigrations(),
                LegacyHelper.GetLegacyEnumTypeNames(),
                LegacyHelper.GetLegacyTypeAliases());

            return SerializationUtility.DeserializeValue<GameInstance>(reader);
        }
    }
}
