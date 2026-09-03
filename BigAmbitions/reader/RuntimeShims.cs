using System;
using System.Reflection;
using HarmonyLib;

namespace BigAmbitionsMonitor
{
    /// <summary>
    /// Игровые DLL собраны под Unity/Mono. Чтобы десериализовать сейв в обычном .NET-процессе,
    /// нужны два рантайм-обхода (применяются только в этом процессе, игру не трогают):
    ///   1) UnityEngine.Debug.* вне плеера падает на нативном вызове — глушим все перегрузки Log*.
    ///      Без этого статический конструктор OdinSerializer.ArchitectureInfo (зовёт Debug.Log)
    ///      бросает исключение и «отравляет» весь тип.
    ///   2) OdinSerializer при emit-форматтерах вызывает устаревший
    ///      AssemblyBuilder.DefineDynamicModule(string, bool), которого нет в .NET Core.
    ///      Форсим EmitUtilities.CanEmit=false — Odin переходит на ReflectionFormatter.
    /// </summary>
    internal static class RuntimeShims
    {
        private static readonly Harmony H = new Harmony("bigambitions.monitor.reader");

        public static void Apply()
        {
            SilenceUnityDebug();
            DisableOdinEmit();
        }

        private static void SilenceUnityDebug()
        {
            var debug = Type.GetType("UnityEngine.Debug, UnityEngine.CoreModule")
                        ?? Type.GetType("UnityEngine.Debug, UnityEngine");
            if (debug == null) return;

            var skip = new HarmonyMethod(typeof(RuntimeShims).GetMethod(nameof(SkipVoid), BindingFlags.NonPublic | BindingFlags.Static));
            foreach (var m in debug.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name.StartsWith("Log") && m.ReturnType == typeof(void) && !m.IsGenericMethod)
                {
                    try { H.Patch(m, prefix: skip); } catch { /* перегрузку пропускаем */ }
                }
            }
        }

        private static void DisableOdinEmit()
        {
            var emitUtil = Type.GetType("OdinSerializer.Utilities.EmitUtilities, OdinSerializer");
            var getter = emitUtil?.GetProperty("CanEmit", BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
            if (getter == null) return;
            var prefix = new HarmonyMethod(typeof(RuntimeShims).GetMethod(nameof(CanEmitFalse), BindingFlags.NonPublic | BindingFlags.Static));
            H.Patch(getter, prefix: prefix);
        }

        private static bool SkipVoid() => false;
        private static bool CanEmitFalse(ref bool __result) { __result = false; return false; }
    }
}
