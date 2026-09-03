using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Патчит BepInEx.Preloader.PlatformUtils.SetPlatform: убирает вызов Module.GetPEKind
// (метод вырезан из mscorlib игры) и жёстко задаёт платформу Windows x64.
// Использование: patch <путь к BepInEx.Preloader.dll>

class Program
{
    static int Main(string[] args)
    {
        var path = args.Length > 0 ? args[0]
            : @"D:\Big.Ambitions.v1.0\Big Ambitions\BepInEx\core\BepInEx.Preloader.dll";

        var asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { ReadWrite = false });

        var type = asm.MainModule.GetType("BepInEx.Preloader.PlatformUtils");
        if (type == null) { Console.Error.WriteLine("Тип PlatformUtils не найден"); return 2; }

        var m = type.Methods.FirstOrDefault(x => x.Name == "SetPlatform");
        if (m == null) { Console.Error.WriteLine("Метод SetPlatform не найден"); return 3; }

        // Находим уже используемую ссылку на сеттер PlatformHelper.set_Current(Platform)
        var setCurrent = m.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            .Select(i => i.Operand as MethodReference)
            .FirstOrDefault(r => r != null && r.Name == "set_Current");
        if (setCurrent == null) { Console.Error.WriteLine("Не нашёл set_Current в SetPlatform"); return 4; }

        // Windows(0x25) | Bits64(0x2) = 0x27
        const int WindowsX64 = 0x27;

        var il = m.Body.GetILProcessor();
        m.Body.Instructions.Clear();
        m.Body.Variables.Clear();
        m.Body.ExceptionHandlers.Clear();
        il.Append(il.Create(OpCodes.Ldc_I4, WindowsX64));
        il.Append(il.Create(OpCodes.Call, setCurrent));
        il.Append(il.Create(OpCodes.Ret));

        asm.Write(path + ".patched");
        Console.WriteLine("OK: записан " + path + ".patched");
        return 0;
    }
}
