using System.Reflection;
using System.Runtime.InteropServices;

namespace Prospect.Client.Platform;

/// <summary>
/// Binds native imports ahead of their first call. The runtime binds a P/Invoke lazily, on its first call, and that
/// binding takes about 32 bytes of managed memory on the calling thread. An import that is first called from an
/// event (the pointer leaving the window, a change of focus, the first resize freeing a render target) would
/// therefore allocate inside the frame loop, at whatever moment the event happens. Binding every import of the
/// libraries the frame loop calls into once at startup moves that cost out of it.
/// </summary>
internal static class NativeBindings
{
    /// <summary>
    /// Binds every native import declared in the given assemblies. Call after their native libraries can be
    /// loaded. Returns how many were bound; an import whose library or entry point is missing is skipped, as calling
    /// it would fail in the same way.
    /// </summary>
    public static int Prelink(params ReadOnlySpan<Assembly> assemblies)
    {
        const BindingFlags Declared = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var bound = 0;
        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                foreach (var method in type.GetMethods(Declared))
                {
                    // Source-generated imports ([LibraryImport]) are wrappers around a private extern method, which
                    // is the one the runtime binds.
                    if ((method.Attributes & MethodAttributes.PinvokeImpl) == 0)
                        continue;
                    try
                    {
                        Marshal.Prelink(method);
                        bound++;
                    }
                    catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException)
                    {
                    }
                }
            }
        }

        return bound;
    }
}
