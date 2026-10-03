using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace WinForge.Component.Benchmark.Graphics;

/// <summary>
/// Interoperabilidad con OpenGL en Windows: contexto por WGL, carga de funciones y las constantes
/// que usa el backend.
///
/// Dos cosas que hay que saber para leerlo:
///
/// <list type="bullet">
/// <item><c>opengl32.dll</c> solo exporta las funciones de OpenGL 1.1. Todo lo demás (VAOs,
/// shaders, queries, instancing) se consigue con <c>wglGetProcAddress</c>, que además devuelve 0
/// para las funciones de 1.1: por eso <see cref="Resolve"/> prueba primero WGL y después
/// <c>GetProcAddress</c> sobre el módulo. Es el baile obligatorio de OpenGL en Windows, y no hay
/// forma de evitarlo sin una biblioteca externa.</item>
/// <item>Las funciones se guardan como PUNTEROS a función y no como delegados: no hay que
/// mantenerlos vivos ni pasar por el marshalling, y el costo de la llamada es el mismo que el de
/// una función nativa.</item>
/// </list>
/// </summary>
internal static unsafe class GlApi
{
    internal const string OpenGlLibrary = "opengl32.dll";

    // ---- Constantes ----
    internal const uint GL_VERSION = 0x1F02;
    internal const uint GL_VENDOR = 0x1F00;
    internal const uint GL_RENDERER = 0x1F01;
    internal const uint GL_SHADING_LANGUAGE_VERSION = 0x8B8C;
    internal const uint GL_EXTENSIONS = 0x1F03;
    internal const uint GL_TRIANGLES = 0x0004;
    internal const uint GL_COLOR_BUFFER_BIT = 0x00004000;
    internal const uint GL_DEPTH_BUFFER_BIT = 0x00000100;
    internal const uint GL_DEPTH_TEST = 0x0B71;
    internal const uint GL_CULL_FACE = 0x0B44;
    internal const uint GL_LESS = 0x0201;
    internal const uint GL_VERTEX_SHADER = 0x8B31;
    internal const uint GL_FRAGMENT_SHADER = 0x8B30;
    internal const uint GL_COMPILE_STATUS = 0x8B81;
    internal const uint GL_LINK_STATUS = 0x8B82;
    internal const uint GL_INFO_LOG_LENGTH = 0x8B84;
    internal const uint GL_ARRAY_BUFFER = 0x8892;
    internal const uint GL_UNIFORM_BUFFER = 0x8A11;
    internal const uint GL_ACTIVE_UNIFORM_BLOCKS = 0x8A36;
    // 0x90D2 es el punto de enlace (y el target de glBindBuffer/glBufferData); 0x90D6, que es el
    // que estaba antes, es GL_MAX_VERTEX_SHADER_STORAGE_BLOCKS: un ENUM de consulta, no un target.
    // Con el número cambiado, las tres llamadas del búfer de la franja devuelven GL_INVALID_ENUM,
    // el búfer nunca llega a tener datos y el shader lee ceros —sin ningún error visible—, así que
    // la franja de métricas no se dibuja y no hay ni un mensaje que lo explique.
    internal const uint GL_SHADER_STORAGE_BUFFER = 0x90D2;
    internal const uint GL_STATIC_DRAW = 0x88E4;
    internal const uint GL_DYNAMIC_DRAW = 0x88E8;
    internal const uint GL_FLOAT = 0x1406;
    internal const uint GL_TIME_ELAPSED = 0x88BF;
    internal const uint GL_QUERY_RESULT = 0x8866;
    internal const uint GL_QUERY_RESULT_AVAILABLE = 0x8867;
    internal const uint GL_CONTEXT_LOST = 0x0507;
    internal const uint GL_GPU_MEMORY_INFO_TOTAL_AVAILABLE_MEMORY_NVX = 0x9048;

    // ---- Franja de métricas: las texturas de sus píxeles y el blend premultiplicado ----
    internal const uint GL_TEXTURE0 = 0x84C0;

    // ---- Textura de detalle de la escena (2D, ver SceneDetailTexture) ----
    internal const uint GL_TEXTURE_2D = 0x0DE1;
    internal const uint GL_RGBA = 0x1908;
    internal const uint GL_RGBA8 = 0x8058;
    internal const uint GL_UNSIGNED_BYTE = 0x1401;
    internal const uint GL_LINEAR = 0x2601;
    internal const uint GL_REPEAT = 0x2901;
    internal const uint GL_TEXTURE_WRAP_S = 0x2802;
    internal const uint GL_TEXTURE_WRAP_T = 0x2803;
    internal const uint GL_TEXTURE_MIN_FILTER = 0x2801;
    internal const uint GL_TEXTURE_MAG_FILTER = 0x2800;

    // ---- Texturas de MATERIAL de la escena (arreglo 2D, ver MaterialAtlas) ----
    // GL_TEXTURE_2D_ARRAY y glTexImage3D son de OpenGL 3.0/1.2 (núcleo en 3.3, así que están en
    // cualquier contexto del perfil core que la escena pida); el filtro con mips lo necesita el
    // arreglo porque sus mips los genera el CPU (no hay glGenerateMipmap).
    internal const uint GL_TEXTURE_2D_ARRAY = 0x8C1A;
    internal const uint GL_TEXTURE_WRAP_R = 0x8072;
    internal const uint GL_LINEAR_MIPMAP_LINEAR = 0x2703;
    internal const uint GL_BLEND = 0x0BE2;
    internal const uint GL_ONE = 1;
    internal const uint GL_ONE_MINUS_SRC_ALPHA = 0x0303;

    // ---- Shadow map (ver README-SOMBRAS.md) ----
    // El mapa de profundidad vive en un framebuffer propio (el de la ventana es de solo-lectura para
    // esto) y se muestrea con el comparador del hardware: la textura lleva GL_TEXTURE_COMPARE_MODE, así
    // que el shader la declara sampler2DShadow y el muestreo devuelve 0/1.
    internal const uint GL_FRAMEBUFFER = 0x8D40;
    internal const uint GL_DEPTH_ATTACHMENT = 0x8D00;
    internal const uint GL_FRAMEBUFFER_COMPLETE = 0x8CD5;
    internal const uint GL_DEPTH_COMPONENT = 0x1902;
    internal const uint GL_DEPTH_COMPONENT24 = 0x81A6;
    internal const uint GL_TEXTURE_COMPARE_MODE = 0x884C;
    internal const uint GL_TEXTURE_COMPARE_FUNC = 0x884D;
    internal const uint GL_COMPARE_REF_TO_TEXTURE = 0x884E;
    internal const uint GL_CLAMP_TO_EDGE = 0x812F;
    /// <summary>GL_NONE: con el framebuffer del mapa no hay NINGÚN adjunto de color, y GL exige
    /// declararlo explícito (con el draw buffer por defecto apuntando a COLOR_ATTACHMENT0 el
    /// framebuffer queda INCOMPLETO y todos los dibujos fallan).</summary>
    internal const uint GL_NONE = 0;

    /// <summary>WGL_EXT_swap_control: 0 = sin vsync.</summary>
    internal const int WGL_SWAP_INTERVAL = 0x20F1;

    // PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER
    internal const uint PfdDrawToWindow = 0x00000004;
    internal const uint PfdSupportOpenGl = 0x00000020;
    internal const uint PfdDoubleBuffer = 0x00000001;

    internal const byte PfdTypeRgba = 0;

    // ---- gdi32 / user32 / opengl32 ----
    [StructLayout(LayoutKind.Sequential)]
    internal struct PixelFormatDescriptor
    {
        public ushort nSize;
        public ushort nVersion;
        public uint dwFlags;
        public byte iPixelType;
        public byte cColorBits;
        public byte cRedBits;
        public byte cRedShift;
        public byte cGreenBits;
        public byte cGreenShift;
        public byte cBlueBits;
        public byte cBlueShift;
        public byte cAlphaBits;
        public byte cAlphaShift;
        public byte cAccumBits;
        public byte cAccumRedBits;
        public byte cAccumGreenBits;
        public byte cAccumBlueBits;
        public byte cAccumAlphaBits;
        public byte cDepthBits;
        public byte cStencilBits;
        public byte cAuxBuffers;
        public byte iLayerType;
        public byte bReserved;
        public uint dwLayerMask;
        public uint dwVisibleMask;
        public uint dwDamageMask;
    }

    [DllImport("gdi32.dll", EntryPoint = "ChoosePixelFormat")]
    internal static extern int ChoosePixelFormat(nint hdc, ref PixelFormatDescriptor descriptor);

    [DllImport("gdi32.dll", EntryPoint = "SetPixelFormat")]
    internal static extern int SetPixelFormat(nint hdc, int format, ref PixelFormatDescriptor descriptor);

    [DllImport("gdi32.dll", EntryPoint = "SwapBuffers")]
    internal static extern int SwapBuffers(nint hdc);

    [DllImport("user32.dll", EntryPoint = "GetDC")]
    internal static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll", EntryPoint = "ReleaseDC")]
    internal static extern int ReleaseDC(nint hwnd, nint hdc);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", EntryPoint = "DestroyWindow")]
    internal static extern int DestroyWindow(nint hwnd);

    [DllImport(OpenGlLibrary, EntryPoint = "wglCreateContext")]
    internal static extern nint wglCreateContext(nint hdc);

    [DllImport(OpenGlLibrary, EntryPoint = "wglDeleteContext")]
    internal static extern int wglDeleteContext(nint context);

    [DllImport(OpenGlLibrary, EntryPoint = "wglMakeCurrent")]
    internal static extern int wglMakeCurrent(nint hdc, nint context);

    [DllImport(OpenGlLibrary, EntryPoint = "wglGetProcAddress")]
    internal static extern nint wglGetProcAddress(string name);

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryW", CharSet = CharSet.Unicode)]
    private static extern nint LoadLibrary(string fileName);

    [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", CharSet = CharSet.Ansi)]
    private static extern nint GetProcAddress(nint module, string name);

    [DllImport("opengl32.dll", EntryPoint = "glGetString")]
    internal static extern nint glGetString(uint name);

    [DllImport("opengl32.dll", EntryPoint = "glGetError")]
    internal static extern uint glGetError();

    // ---- Funciones modernas (punteros cargados al crear el contexto) ----
    internal static delegate* unmanaged[Cdecl]<uint, uint> glCreateShader;
    internal static delegate* unmanaged[Cdecl]<uint, int, nint*, int*, void> glShaderSource;
    internal static delegate* unmanaged[Cdecl]<uint, void> glCompileShader;
    internal static delegate* unmanaged[Cdecl]<uint, uint, int*, void> glGetShaderiv;
    internal static delegate* unmanaged[Cdecl]<uint, int, int*, byte*, void> glGetShaderInfoLog;
    internal static delegate* unmanaged[Cdecl]<uint, void> glDeleteShader;
    internal static delegate* unmanaged[Cdecl]<uint> glCreateProgram;
    internal static delegate* unmanaged[Cdecl]<uint, uint, void> glAttachShader;
    internal static delegate* unmanaged[Cdecl]<uint, void> glLinkProgram;
    internal static delegate* unmanaged[Cdecl]<uint, uint, int*, void> glGetProgramiv;
    internal static delegate* unmanaged[Cdecl]<uint, int, int*, byte*, void> glGetProgramInfoLog;
    internal static delegate* unmanaged[Cdecl]<uint, void> glUseProgram;
    internal static delegate* unmanaged[Cdecl]<uint, void> glDeleteProgram;
    internal static delegate* unmanaged[Cdecl]<int, uint*, void> glGenVertexArrays;
    internal static delegate* unmanaged[Cdecl]<uint, void> glBindVertexArray;
    internal static delegate* unmanaged[Cdecl]<int, uint*, void> glDeleteVertexArrays;
    internal static delegate* unmanaged[Cdecl]<int, uint*, void> glGenBuffers;
    internal static delegate* unmanaged[Cdecl]<uint, uint, void> glBindBuffer;
    internal static delegate* unmanaged[Cdecl]<uint, nint, void*, uint, void> glBufferData;
    internal static delegate* unmanaged[Cdecl]<int, uint*, void> glDeleteBuffers;
    internal static delegate* unmanaged[Cdecl]<uint, int, uint, byte, int, void*, void> glVertexAttribPointer;
    internal static delegate* unmanaged[Cdecl]<uint, void> glEnableVertexAttribArray;
    internal static delegate* unmanaged[Cdecl]<uint, uint, void> glVertexAttribDivisor;
    internal static delegate* unmanaged[Cdecl]<uint, byte*, uint> glGetUniformBlockIndex;
    internal static delegate* unmanaged[Cdecl]<uint, uint, uint, void> glUniformBlockBinding;
    internal static delegate* unmanaged[Cdecl]<uint, uint, uint, void> glBindBufferBase;
    internal static delegate* unmanaged[Cdecl]<int, uint*, void> glGenQueries;
    internal static delegate* unmanaged[Cdecl]<int, uint*, void> glDeleteQueries;
    internal static delegate* unmanaged[Cdecl]<uint, uint, void> glBeginQuery;
    internal static delegate* unmanaged[Cdecl]<uint, void> glEndQuery;
    internal static delegate* unmanaged[Cdecl]<uint, uint, int*, void> glGetQueryObjectiv;
    internal static delegate* unmanaged[Cdecl]<uint, uint, ulong*, void> glGetQueryObjectui64v;
    internal static delegate* unmanaged[Cdecl]<uint, int, int, int, void> glDrawArraysInstanced;
    internal static delegate* unmanaged[Cdecl]<float, float, float, float, void> glClearColor;
    internal static delegate* unmanaged[Cdecl]<uint, void> glClear;
    internal static delegate* unmanaged[Cdecl]<int, int, int, int, void> glViewport;
    internal static delegate* unmanaged[Cdecl]<uint, void> glEnable;
    internal static delegate* unmanaged[Cdecl]<uint, void> glDisable;
    internal static delegate* unmanaged[Cdecl]<uint, void> glDepthFunc;
    internal static delegate* unmanaged[Cdecl]<byte, void> glDepthMask;

    // ---- Franja de métricas (ver OverlayFrame y OpenGLBackend.DrawOverlay) ----
    internal static delegate* unmanaged[Cdecl]<int, uint*, void> glGenTextures;
    internal static delegate* unmanaged[Cdecl]<int, uint*, void> glDeleteTextures;
    internal static delegate* unmanaged[Cdecl]<uint, uint, void> glBindTexture;
    internal static delegate* unmanaged[Cdecl]<uint, void> glActiveTexture;

    /// <summary>glTexImage2D y glTexParameteri son de OpenGL 1.1: las exporta el propio opengl32.dll.</summary>
    internal static delegate* unmanaged[Cdecl]<uint, int, int, int, int, int, uint, uint, void*, void> glTexImage2D;
    internal static delegate* unmanaged[Cdecl]<uint, uint, int, void> glTexParameteri;

    /// <summary>glTexImage3D (una rebanada del arreglo de materiales por llamada, con su nivel de mip).</summary>
    internal static delegate* unmanaged[Cdecl]<uint, int, int, int, int, int, int, uint, uint, void*, void> glTexImage3D;
    internal static delegate* unmanaged[Cdecl]<uint, uint, void> glBlendFunc;

    /// <summary>glDrawArrays: es de OpenGL 1.1, así que la exporta el propio opengl32.dll.</summary>
    internal static delegate* unmanaged[Cdecl]<uint, int, int, void> glDrawArrays;

    // ---- Framebuffer del shadow map (ver README-SOMBRAS.md) ----
    internal static delegate* unmanaged[Cdecl]<int, uint*, void> glGenFramebuffers;
    internal static delegate* unmanaged[Cdecl]<int, uint*, void> glDeleteFramebuffers;
    internal static delegate* unmanaged[Cdecl]<uint, uint, void> glBindFramebuffer;
    internal static delegate* unmanaged[Cdecl]<uint, uint, uint, uint, int, void> glFramebufferTexture2D;
    // glDrawBuffer/glReadBuffer toman SOLO el modo: el framebuffer es el que está atado.
    internal static delegate* unmanaged[Cdecl]<uint, void> glDrawBuffer;
    internal static delegate* unmanaged[Cdecl]<uint, void> glReadBuffer;
    internal static delegate* unmanaged[Cdecl]<uint, uint> glCheckFramebufferStatus;

    /// <summary>wglCreateContextAttribsARB / wglSwapIntervalEXT (opcionales).</summary>
    internal static delegate* unmanaged[Cdecl]<nint, nint, int*, nint> wglCreateContextAttribsARB;
    internal static delegate* unmanaged[Cdecl]<int, int> wglSwapIntervalEXT;

    private static nint _module;
    private static bool _loaded;

    /// <summary>¿Está la biblioteca del sistema? Se pregunta por el archivo: "no hay OpenGL" y
    /// "el driver no soporta lo que la escena necesita" son motivos distintos.</summary>
    internal static bool IsLibraryPresent
    {
        get
        {
            try
            {
                string system = Environment.SystemDirectory;
                if (!string.IsNullOrEmpty(system) && File.Exists(Path.Combine(system, OpenGlLibrary))) return true;
                return File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), OpenGlLibrary));
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Resuelve las funciones de WGL. Se llama con CUALQUIER contexto actual (normalmente el
    /// heredado) y ANTES de pedir el contexto definitivo: sin <c>wglCreateContextAttribsARB</c> no
    /// se puede pedir un perfil core, y su dirección no existe hasta que hay un contexto vivo.
    /// </summary>
    internal static void ResolveWglExtensions()
    {
        if (_module == 0) _module = LoadLibrary(OpenGlLibrary);
        if (_module == 0) return;

        wglCreateContextAttribsARB = (delegate* unmanaged[Cdecl]<nint, nint, int*, nint>)Resolve("wglCreateContextAttribsARB");
        wglSwapIntervalEXT = (delegate* unmanaged[Cdecl]<int, int>)Resolve("wglSwapIntervalEXT");
    }

    /// <summary>
    /// Carga las funciones de OpenGL modernas. Necesita un contexto YA actual, porque es WGL quien
    /// las resuelve. Devuelve false si falta alguna imprescindible: sin ellas no se dibuja nada.
    /// </summary>
    internal static bool Load()
    {
        if (_loaded) return true;
        if (_module == 0) _module = LoadLibrary(OpenGlLibrary);
        if (_module == 0) return false;

        glCreateShader = (delegate* unmanaged[Cdecl]<uint, uint>)Resolve("glCreateShader");
        glShaderSource = (delegate* unmanaged[Cdecl]<uint, int, nint*, int*, void>)Resolve("glShaderSource");
        glCompileShader = (delegate* unmanaged[Cdecl]<uint, void>)Resolve("glCompileShader");
        glGetShaderiv = (delegate* unmanaged[Cdecl]<uint, uint, int*, void>)Resolve("glGetShaderiv");
        glGetShaderInfoLog = (delegate* unmanaged[Cdecl]<uint, int, int*, byte*, void>)Resolve("glGetShaderInfoLog");
        glDeleteShader = (delegate* unmanaged[Cdecl]<uint, void>)Resolve("glDeleteShader");
        glCreateProgram = (delegate* unmanaged[Cdecl]<uint>)Resolve("glCreateProgram");
        glAttachShader = (delegate* unmanaged[Cdecl]<uint, uint, void>)Resolve("glAttachShader");
        glLinkProgram = (delegate* unmanaged[Cdecl]<uint, void>)Resolve("glLinkProgram");
        glGetProgramiv = (delegate* unmanaged[Cdecl]<uint, uint, int*, void>)Resolve("glGetProgramiv");
        glGetProgramInfoLog = (delegate* unmanaged[Cdecl]<uint, int, int*, byte*, void>)Resolve("glGetProgramInfoLog");
        glUseProgram = (delegate* unmanaged[Cdecl]<uint, void>)Resolve("glUseProgram");
        glDeleteProgram = (delegate* unmanaged[Cdecl]<uint, void>)Resolve("glDeleteProgram");
        glGenVertexArrays = (delegate* unmanaged[Cdecl]<int, uint*, void>)Resolve("glGenVertexArrays");
        glBindVertexArray = (delegate* unmanaged[Cdecl]<uint, void>)Resolve("glBindVertexArray");
        glDeleteVertexArrays = (delegate* unmanaged[Cdecl]<int, uint*, void>)Resolve("glDeleteVertexArrays");
        glGenBuffers = (delegate* unmanaged[Cdecl]<int, uint*, void>)Resolve("glGenBuffers");
        glBindBuffer = (delegate* unmanaged[Cdecl]<uint, uint, void>)Resolve("glBindBuffer");
        glBufferData = (delegate* unmanaged[Cdecl]<uint, nint, void*, uint, void>)Resolve("glBufferData");
        glDeleteBuffers = (delegate* unmanaged[Cdecl]<int, uint*, void>)Resolve("glDeleteBuffers");
        glVertexAttribPointer = (delegate* unmanaged[Cdecl]<uint, int, uint, byte, int, void*, void>)Resolve("glVertexAttribPointer");
        glEnableVertexAttribArray = (delegate* unmanaged[Cdecl]<uint, void>)Resolve("glEnableVertexAttribArray");
        glVertexAttribDivisor = (delegate* unmanaged[Cdecl]<uint, uint, void>)Resolve("glVertexAttribDivisor");
        glGetUniformBlockIndex = (delegate* unmanaged[Cdecl]<uint, byte*, uint>)Resolve("glGetUniformBlockIndex");
        glUniformBlockBinding = (delegate* unmanaged[Cdecl]<uint, uint, uint, void>)Resolve("glUniformBlockBinding");
        glBindBufferBase = (delegate* unmanaged[Cdecl]<uint, uint, uint, void>)Resolve("glBindBufferBase");
        glGenQueries = (delegate* unmanaged[Cdecl]<int, uint*, void>)Resolve("glGenQueries");
        glDeleteQueries = (delegate* unmanaged[Cdecl]<int, uint*, void>)Resolve("glDeleteQueries");
        glBeginQuery = (delegate* unmanaged[Cdecl]<uint, uint, void>)Resolve("glBeginQuery");
        glEndQuery = (delegate* unmanaged[Cdecl]<uint, void>)Resolve("glEndQuery");
        glGetQueryObjectiv = (delegate* unmanaged[Cdecl]<uint, uint, int*, void>)Resolve("glGetQueryObjectiv");
        glGetQueryObjectui64v = (delegate* unmanaged[Cdecl]<uint, uint, ulong*, void>)Resolve("glGetQueryObjectui64v");
        glDrawArraysInstanced = (delegate* unmanaged[Cdecl]<uint, int, int, int, void>)Resolve("glDrawArraysInstanced");
        glClearColor = (delegate* unmanaged[Cdecl]<float, float, float, float, void>)Resolve("glClearColor");
        glClear = (delegate* unmanaged[Cdecl]<uint, void>)Resolve("glClear");
        glViewport = (delegate* unmanaged[Cdecl]<int, int, int, int, void>)Resolve("glViewport");
        glEnable = (delegate* unmanaged[Cdecl]<uint, void>)Resolve("glEnable");
        glDisable = (delegate* unmanaged[Cdecl]<uint, void>)Resolve("glDisable");
        glDepthFunc = (delegate* unmanaged[Cdecl]<uint, void>)Resolve("glDepthFunc");
        glDepthMask = (delegate* unmanaged[Cdecl]<byte, void>)Resolve("glDepthMask");
        glGenTextures = (delegate* unmanaged[Cdecl]<int, uint*, void>)Resolve("glGenTextures");
        glDeleteTextures = (delegate* unmanaged[Cdecl]<int, uint*, void>)Resolve("glDeleteTextures");
        glBindTexture = (delegate* unmanaged[Cdecl]<uint, uint, void>)Resolve("glBindTexture");
        glActiveTexture = (delegate* unmanaged[Cdecl]<uint, void>)Resolve("glActiveTexture");
        glTexImage2D = (delegate* unmanaged[Cdecl]<uint, int, int, int, int, int, uint, uint, void*, void>)Resolve("glTexImage2D");
        glTexParameteri = (delegate* unmanaged[Cdecl]<uint, uint, int, void>)Resolve("glTexParameteri");
        glTexImage3D = (delegate* unmanaged[Cdecl]<uint, int, int, int, int, int, int, uint, uint, void*, void>)Resolve("glTexImage3D");
        glBlendFunc = (delegate* unmanaged[Cdecl]<uint, uint, void>)Resolve("glBlendFunc");
        glDrawArrays = (delegate* unmanaged[Cdecl]<uint, int, int, void>)Resolve("glDrawArrays");
        glGenFramebuffers = (delegate* unmanaged[Cdecl]<int, uint*, void>)Resolve("glGenFramebuffers");
        glDeleteFramebuffers = (delegate* unmanaged[Cdecl]<int, uint*, void>)Resolve("glDeleteFramebuffers");
        glBindFramebuffer = (delegate* unmanaged[Cdecl]<uint, uint, void>)Resolve("glBindFramebuffer");
        glFramebufferTexture2D = (delegate* unmanaged[Cdecl]<uint, uint, uint, uint, int, void>)Resolve("glFramebufferTexture2D");
        glDrawBuffer = (delegate* unmanaged[Cdecl]<uint, void>)Resolve("glDrawBuffer");
        glReadBuffer = (delegate* unmanaged[Cdecl]<uint, void>)Resolve("glReadBuffer");
        glCheckFramebufferStatus = (delegate* unmanaged[Cdecl]<uint, uint>)Resolve("glCheckFramebufferStatus");

        _loaded = glCreateShader != null && glCreateProgram != null && glGenVertexArrays != null &&
                  glDrawArraysInstanced != null && glGenQueries != null && glBufferData != null;
        return _loaded;
    }

    /// <summary>Resuelve una función: primero WGL (todo lo posterior a 1.1) y después el módulo.</summary>
    private static nint Resolve(string name)
    {
        try
        {
            nint address = wglGetProcAddress(name);
            // WGL devuelve valores "basura" (1, 2, 3, -1) cuando no encuentra la función.
            if (address != 0 && address != 1 && address != 2 && address != 3 && address != -1) return address;
        }
        catch { }
        try { return GetProcAddress(_module, name); }
        catch { return 0; }
    }

    /// <summary>Un valor entero del driver (glGetIntegerv, que es de OpenGL 1.0).</summary>
    internal static uint GetInteger(uint name)
    {
        uint value = 0;
        glGetIntegerv(name, &value);
        return value;
    }

    [DllImport(OpenGlLibrary, EntryPoint = "glGetIntegerv")]
    private static extern void glGetIntegerv(uint name, uint* value);

    /// <summary>Texto de una cadena de OpenGL (null si no hay contexto o la constante no aplica).</summary>
    internal static string? QueryString(uint name)
    {
        try
        {
            nint pointer = glGetString(name);
            return pointer == 0 ? null : Marshal.PtrToStringUTF8(pointer);
        }
        catch { return null; }
    }

    /// <summary>Extensión disponible (se consulta el listado que ya devolvió el driver).</summary>
    internal static bool HasExtension(string? extensions, string name) =>
        extensions != null && extensions.Split(' ').Contains(name);
}
