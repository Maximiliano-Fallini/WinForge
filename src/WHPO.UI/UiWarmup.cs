using System.Reflection;
using System.Runtime.CompilerServices;
using WHPO_UI.Views.Pages;

namespace WHPO_UI;

/// <summary>
/// Pre-compila (JIT) en segundo plano los tipos pesados de la primera pantalla.
///
/// Por qué existe: en la build publicada el IL viene pre-compilado (ReadyToRun),
/// pero en la build de desarrollo (Debug) no. Sin esto, crear <see cref="MainWindow"/>
/// y navegar a <see cref="SistemaPage"/> se "traba" en el hilo de UI compilando los
/// métodos de ambos (InitializeComponent, Connect, handlers de eventos, traducción,
/// apariencia) justo cuando el splash está por cerrar: el splash se congelaba cerca
/// del 76 % y recién se liberaba al aparecer la ventana.
///
/// La solución: disparar esta compilación en un hilo en segundo plano MIENTRAS el
/// splash está en sus fases de I/O (idiomas/config), cuando el hilo de UI está libre y
/// el splash puede seguir animando. Cuando después se construye la ventana, los métodos
/// ya están compilados y la construcción queda rápida.
///
/// Es seguro: <see cref="RuntimeHelpers.PrepareMethod(RuntimeMethodHandle)"/> SOLO compila el
/// método, no lo ejecuta, así que no hay efectos secundarios por correr fuera del hilo
/// de UI. Cada método va en su propio try/catch (los genéricos o raros pueden fallar y
/// no deben tumbar el arranque).
/// </summary>
internal static class UiWarmup
{
    public static void Prepare()
    {
        PrepareType(typeof(MainWindow));
        PrepareType(typeof(SistemaPage));
    }

    private static void PrepareType(Type? type)
    {
        if (type is null) return;

        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic
                                 | BindingFlags.Instance | BindingFlags.Static
                                 | BindingFlags.DeclaredOnly;

        MethodInfo[] methods;
        try { methods = type.GetMethods(Flags); }
        catch { return; }

        foreach (var m in methods)
        {
            try { RuntimeHelpers.PrepareMethod(m.MethodHandle); }
            catch { /* un método no preparable no debe frenar el resto */ }
        }

        Type[] nested;
        try { nested = type.GetNestedTypes(Flags); }
        catch { return; }
        foreach (var n in nested)
            PrepareType(n);
    }
}
