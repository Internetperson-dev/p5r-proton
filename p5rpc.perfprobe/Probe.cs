using System.Diagnostics;
using System.Runtime.InteropServices;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace p5rpc.perfprobe;

/// <summary>
/// Counts D3D11/DXGI calls made through the vtable and Windows API calls made through module IATs. It keeps rings
/// of return addresses (and first arguments for Windows calls), adds primitive totals for draws, and reports FPS
/// and process CPU. Everything in the hot path is native: no managed callbacks, no allocations, no logging.
///
/// Counting at the vtable/IAT (rather than at the game's call sites) means the probe sees calls from every module,
/// including uploads rewritten into UpdateSubresource1 by p5rpc.performancefix, and can say which callers are
/// responsible.
/// </summary>
public static unsafe class Probe
{
    /// <summary>Ring entries for full caller-diagnostic mode. 16384 * 8 = 128 KB per ring.</summary>
    public const int RingEntries = 16384;

    /// <summary>
    /// Efficient mode only needs frame-time percentiles, so a 256-entry ring keeps the newest ~3 s at 80 FPS
    /// while fitting in 2 KB instead of 128 KB. Full caller mode is unchanged.
    /// </summary>
    public const int EfficientRingEntries = 256;

    // Stub code is padded to 0x100 bytes, so all data slots must start after it.
    // Keep counters near the code; large rings go after them so efficient stubs need only a tiny allocation.
    private const int CodeSize = 0x100;
    private const int IdxOffset = CodeSize;
    private const int OriginalOffset = CodeSize + 8;
    private const int TotalOffset = CodeSize + 0x10;
    private const int CyclesOffset = TotalOffset + 8;
    private const int ArgOffset = CyclesOffset + 8;
    private const int RingOffset = ArgOffset + RingEntries * 8;
    private const int BlockSize = RingOffset + RingEntries * 8;
    private const int EfficientBlockSize = ArgOffset + 8;
    private const int EfficientPresentBlockSize = ArgOffset + EfficientRingEntries * 8;

    private enum Kind { Counter, Upload, Draw, DrawInstanced, Arg, Present }

    private sealed record SlotSpec(int Slot, string Name, Kind Kind, bool Default = true);

    /// <summary>ID3D11DeviceContext slots. The default set covers nearly all render work; detailed adds the rest.</summary>
    private static readonly SlotSpec[] ContextSlots =
    [
        new(7, "VSSetConstantBuffers", Kind.Counter),
        new(8, "PSSetShaderResources", Kind.Counter),
        new(9, "PSSetShader", Kind.Counter),
        new(10, "PSSetSamplers", Kind.Counter),
        new(11, "VSSetShader", Kind.Counter),
        new(12, "DrawIndexed", Kind.Draw),
        new(13, "Draw", Kind.Draw),
        new(14, "Map", Kind.Counter),
        new(15, "Unmap", Kind.Counter),
        new(16, "PSSetConstantBuffers", Kind.Counter),
        new(17, "IASetInputLayout", Kind.Counter),
        new(18, "IASetVertexBuffers", Kind.Counter),
        new(19, "IASetIndexBuffer", Kind.Counter),
        new(20, "DrawIndexedInstanced", Kind.DrawInstanced),
        new(21, "DrawInstanced", Kind.DrawInstanced),
        new(22, "GSSetConstantBuffers", Kind.Counter),
        new(23, "GSSetShader", Kind.Counter),
        new(24, "IASetPrimitiveTopology", Kind.Counter),
        new(25, "VSSetShaderResources", Kind.Counter),
        new(26, "VSSetSamplers", Kind.Counter),
        new(33, "OMSetRenderTargets", Kind.Counter),
        new(35, "OMSetBlendState", Kind.Counter),
        new(36, "OMSetDepthStencilState", Kind.Counter),
        new(38, "DrawAuto", Kind.Counter),
        new(39, "DrawIndexedInstancedIndirect", Kind.Counter),
        new(40, "DrawInstancedIndirect", Kind.Counter),
        new(41, "Dispatch", Kind.Counter),
        new(43, "RSSetState", Kind.Counter),
        new(44, "RSSetViewports", Kind.Counter),
        new(45, "RSSetScissorRects", Kind.Counter),
        new(46, "CopySubresourceRegion", Kind.Counter),
        new(47, "CopyResource", Kind.Counter),
        new(48, "UpdateSubresource", Kind.Upload),
        new(50, "ClearRenderTargetView", Kind.Counter),
        new(53, "ClearDepthStencilView", Kind.Counter),
        new(54, "GenerateMips", Kind.Counter),
        new(57, "ResolveSubresource", Kind.Counter),
        new(59, "HSSetShaderResources", Kind.Counter),
        new(60, "HSSetShader", Kind.Counter),
        new(62, "HSSetConstantBuffers", Kind.Counter),
        new(63, "DSSetShaderResources", Kind.Counter),
        new(64, "DSSetShader", Kind.Counter),
        new(66, "DSSetConstantBuffers", Kind.Counter),
        new(67, "CSSetShaderResources", Kind.Counter),
        new(69, "CSSetShader", Kind.Counter),
        new(71, "CSSetConstantBuffers", Kind.Counter),
        new(110, "ClearState", Kind.Counter),
        new(111, "Flush", Kind.Counter),
        new(114, "FinishCommandList", Kind.Counter),
        new(116, "UpdateSubresource1", Kind.Upload),
        new(115, "CopySubresourceRegion1", Kind.Counter, false),
        new(117, "DiscardResource", Kind.Counter, false),
        new(118, "DiscardView", Kind.Counter, false),
        new(119, "VSSetConstantBuffers1", Kind.Counter, false),
        new(120, "HSSetConstantBuffers1", Kind.Counter, false),
        new(121, "DSSetConstantBuffers1", Kind.Counter, false),
        new(122, "GSSetConstantBuffers1", Kind.Counter, false),
        new(123, "PSSetConstantBuffers1", Kind.Counter, false),
        new(124, "CSSetConstantBuffers1", Kind.Counter, false),
    ];

    /// <summary>ID3D11Device slots: resource creation is where area-load hitches appear.</summary>
    private static readonly SlotSpec[] DeviceSlots =
    [
        new(3, "CreateBuffer", Kind.Counter),
        new(4, "CreateTexture1D", Kind.Counter),
        new(5, "CreateTexture2D", Kind.Counter),
        new(6, "CreateTexture3D", Kind.Counter),
        new(7, "CreateShaderResourceView", Kind.Counter),
        new(8, "CreateUnorderedAccessView", Kind.Counter),
        new(9, "CreateRenderTargetView", Kind.Counter),
        new(10, "CreateDepthStencilView", Kind.Counter),
        new(11, "CreateInputLayout", Kind.Counter),
        new(12, "CreateVertexShader", Kind.Counter),
        new(13, "CreateGeometryShader", Kind.Counter),
        new(14, "CreateGeometryShaderWithStreamOutput", Kind.Counter),
        new(15, "CreatePixelShader", Kind.Counter),
        new(16, "CreateHullShader", Kind.Counter),
        new(17, "CreateDomainShader", Kind.Counter),
        new(18, "CreateComputeShader", Kind.Counter),
        new(20, "CreateBlendState", Kind.Counter),
        new(21, "CreateDepthStencilState", Kind.Counter),
        new(22, "CreateRasterizerState", Kind.Counter),
        new(23, "CreateSamplerState", Kind.Counter),
        new(24, "CreateQuery", Kind.Counter),
        new(27, "CreateDeferredContext", Kind.Counter),
        new(28, "OpenSharedResource", Kind.Counter),
        new(29, "CheckFormatSupport", Kind.Counter),
        new(30, "CheckMultisampleQualityLevels", Kind.Counter),
        new(33, "CheckFeatureSupport", Kind.Counter),
    ];

    /// <summary>IDXGISwapChain slots. Present gives FPS; the rest can disturb frame pacing.</summary>
    private static readonly SlotSpec[] SwapSlots =
    [
        new(8, "Present", Kind.Present),
        new(9, "GetBuffer", Kind.Counter),
        new(10, "SetFullscreenState", Kind.Counter),
        new(12, "GetDesc", Kind.Counter),
        new(13, "ResizeBuffers", Kind.Counter),
        new(14, "ResizeTarget", Kind.Counter),
        new(16, "GetFrameStatistics", Kind.Counter),
        new(17, "GetLastPresentCount", Kind.Counter),
    ];

    /// <summary>Minimal set used in efficient mode: enough to explain render cost without per-slot caller rings.</summary>
    private static readonly SlotSpec[] EfficientContextSlots =
    [
        new(12, "DrawIndexed", Kind.Draw),
        new(13, "Draw", Kind.Draw),
        new(14, "Map", Kind.Counter),
        new(15, "Unmap", Kind.Counter),
        new(20, "DrawIndexedInstanced", Kind.DrawInstanced),
        new(21, "DrawInstanced", Kind.DrawInstanced),
        new(48, "UpdateSubresource", Kind.Upload),
        new(111, "Flush", Kind.Counter),
        new(116, "UpdateSubresource1", Kind.Upload),
    ];

    private static readonly SlotSpec[] EfficientDeviceSlots =
    [
        new(3, "CreateBuffer", Kind.Counter),
        new(5, "CreateTexture2D", Kind.Counter),
    ];

    private static readonly SlotSpec[] EfficientSwapSlots =
    [
        new(8, "Present", Kind.Present),
        new(13, "ResizeBuffers", Kind.Counter),
    ];

    private sealed class Slot
    {
        public Slot(string name, int index, Kind kind, nint block, int capacity)
        {
            Name = name;
            Index = index;
            Kind = kind;
            Block = block;
            Capacity = capacity;
        }

        public string Name { get; }
        public int Index { get; }
        public Kind Kind { get; }
        public nint Block { get; }
        public int Capacity { get; }
        public long Count => Volatile.Read(ref *(long*)(Block + IdxOffset));
        public long Total => Kind is Kind.Draw or Kind.DrawInstanced ? Volatile.Read(ref *(long*)(Block + TotalOffset)) : 0;
        public long Cycles => Volatile.Read(ref *(long*)(Block + CyclesOffset));
        public nint Ring => Block + RingOffset;
        public nint ArgRing => Block + ArgOffset;
    }

    private static readonly object Gate = new();
    private static readonly HashSet<(string, nint)> PatchedVtables = [];
    private static readonly HashSet<nint> HookedFunctions = [];
    private static readonly List<Slot> Active = [];
    private static readonly List<object> KeepAlive = [];
    private static readonly Dictionary<Slot, long> PreviousCounts = [];
    private static readonly Dictionary<Slot, long> PreviousTotals = [];
    private static readonly Dictionary<Slot, long> PreviousCycles = [];
    private static readonly HashSet<(nint Iat, string Function)> PatchedImports = [];
    private static readonly HashSet<nint> WindowsModulesSeen = [];

    /// <summary>Windows calls worth counting, with the DLL the game imports them from.</summary>
    private static readonly (string Dll, string Function)[] WindowsImports =
    [
        ("kernel", "GetTickCount"),
        ("kernel", "GetTickCount64"),
        ("kernel", "QueryPerformanceCounter"),
        ("kernel", "Sleep"),
        ("kernel", "SleepEx"),
        ("kernel", "SleepConditionVariableCS"),
        ("kernel", "SleepConditionVariableSRW"),
        ("kernel", "SwitchToThread"),
        ("kernel", "WaitForSingleObject"),
        ("kernel", "WaitForSingleObjectEx"),
        ("kernel", "WaitForMultipleObjects"),
        ("kernel", "WaitForMultipleObjectsEx"),
        ("kernel", "SetProcessAffinityMask"),
        ("kernel", "SetThreadAffinityMask"),
        ("kernel", "GetProcessAffinityMask"),
        ("ntdll", "NtDelayExecution"),
        ("ntdll", "NtYieldExecution"),
        ("ntdll", "NtWaitForSingleObject"),
        ("ntdll", "NtWaitForMultipleObjects"),
        ("user32", "PeekMessageW"),
        ("user32", "PeekMessageA"),
        ("user32", "GetMessageW"),
        ("user32", "MsgWaitForMultipleObjectsEx"),
        ("winmm", "timeBeginPeriod"),
        ("winmm", "timeEndPeriod"),
        ("winmm", "timeGetTime"),
    ];

    private static Action<string>? _log;
    private static IReloadedHooks? _hooks;

    // Originals are called from [UnmanagedCallersOnly] hook entries. Going through a managed lambda makes
    // Reloaded.Hooks enter C# with an unaligned stack on Wine 11, which faults in DXVK builds that use movaps.
    private static CreateDxgiFactoryFn? _originalCreateDxgiFactory;
    private static CreateDxgiFactory2Fn? _originalCreateDxgiFactory2;
    private static D3D11CreateDeviceFn? _originalD3D11CreateDevice;
    private static D3D11CreateDeviceFn? _originalD3D11CoreCreateDevice;
    private static D3D11CreateDeviceSwapChainFn? _originalD3D11CreateDeviceAndSwapChain;

    private static bool _installed, _detailed, _measureTime, _profileWindows, _profileAllModules, _efficient;
    private static double _cyclesPerSecond = 1;
    private static ModuleInfo[] _modules = [];
    private static long _prevWall = Stopwatch.GetTimestamp();
    private static ulong _prevKernel, _prevUser;

    public static void Install(IReloadedHooks? hooks, Action<string> log, bool detailed, bool measureTime,
        bool profileWindows, bool profileAllModules, bool efficient)
    {
        if (_installed) return;
        _installed = true;
        _log = log;
        _hooks = hooks;
        _detailed = detailed;
        _efficient = efficient;
        _profileWindows = profileWindows && !efficient; // P5R has no standard import table; skip the scan
        _profileAllModules = profileAllModules;
        // Forced off: the timed call/ret wrapper has no SEH unwind metadata and
        // can break Wine/Windows exception unwinding. Count-only leaf stubs are safe.
        _measureTime = false;
        log(_efficient
            ? "Efficient mode: minimal native counters + Present frame-time percentiles; no caller rings."
            : "Per-call CPU timing disabled: using unwind-safe count-only stubs.");
        // TSC calibration is needed for Present frame-time percentiles; the old timed call/ret wrapper is still off.
        CalibrateCycles();
        RefreshModules();
        GetProcessTimes(GetCurrentProcess(), out _, out _, out var kernel, out var user);
        _prevKernel = ToUInt64(kernel);
        _prevUser = ToUInt64(user);

        if (_profileWindows)
        {
            var count = InstallWindowsApiHooks();
            log($"Windows API IAT counters installed: {count} imports " +
                $"({(_profileAllModules ? "all non-runtime modules" : "game/graphics/input modules only")}).");
        }
        else
        {
            log("Windows API IAT counters disabled by configuration.");
        }

        if (hooks == null)
        {
            log("Reloaded.Hooks is unavailable; the probe cannot hook device creation. Nothing was instrumented.");
            return;
        }

        // Reloaded holds the game's entry point while mods initialise, so creation is hooked before P5R creates its device.
        var d3d11 = LoadLibraryW("d3d11.dll");
        if (d3d11 == 0)
        {
            log("d3d11.dll could not be loaded; the probe cannot find a device. Nothing was instrumented.");
            return;
        }

        RefreshModules();
        var modules = new HashSet<nint> { d3d11 };
        foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
        {
            try
            {
                if (!module.ModuleName.Equals("d3d11.dll", StringComparison.OrdinalIgnoreCase)) continue;
                if (GetModuleHandleEx(0x2, module.BaseAddress, out var handle)) modules.Add(handle); // FROM_ADDRESS
            }
            catch { /* a module can disappear between enumeration and handle lookup */ }
        }

        foreach (var module in modules)
        {
            HookCreation(module, "D3D11CreateDevice", swapChain: false);
            HookCreation(module, "D3D11CoreCreateDevice", swapChain: false);
            HookCreation(module, "D3D11CreateDeviceAndSwapChain", swapChain: true);
        }

        HookDxgiFactoryCreation();

        if (HookedFunctions.Count == 0)
            log("No D3D11/DXGI creation export was found; the probe will not see any contexts.");
        else
            log($"Detailed mode: {(_detailed ? "on (all useful vtable slots)" : "off (curated render-work slots)")}.");
    }

    /// <summary>
    /// Patch the import tables of already-loaded modules. Called again from the background logger so modules loaded
    /// later are picked up too. The per-call code is a native leaf stub, so this adds no managed work to the game.
    /// </summary>
    private static int InstallWindowsApiHooks()
    {
        var patched = 0;
        foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
        {
            string? name = null;
            try
            {
                name = module.ModuleName;
                if (string.IsNullOrEmpty(name) || module.BaseAddress == 0 || IsExcludedModule(name) ||
                    IsOsModule(name)) continue;
                if (!_profileAllModules && !IsInterestingModule(name)) continue;
                if (!WindowsModulesSeen.Add(module.BaseAddress)) continue;

                var count = PatchModuleImports(module.BaseAddress, module.ModuleMemorySize, name);
                patched += count;
                if (count > 0) Log($"  {name}: {count} Windows import(s) counted.");
            }
            catch (Exception exception)
            {
                Log($"  skipped {name ?? "module"}: {exception.GetType().Name}: {exception.Message}");
            }
        }
        return patched;
    }

    private static int PatchModuleImports(nint image, int imageSize, string moduleName)
    {
        // Bound every read to the mapped image. The old version used fixed 256-byte reads at raw RVAs, which can
        // cross the end of a module and raise an uncatchable access violation under Wine.
        if (imageSize < 0x100) return 0;

        var bytes = (byte*)image;
        if (bytes[0] != (byte)'M' || bytes[1] != (byte)'Z') return 0;
        var peOffset = *(int*)(image + 0x3c);
        if (peOffset <= 0 || peOffset + 144 > imageSize) return 0; // PE header + PE32+ optional prefix + dirs

        var nt = image + peOffset;
        if (*(uint*)nt != 0x00004550) return 0; // "PE\0\0"
        var optional = nt + 4 + 20;
        if (*(ushort*)optional != 0x20b) return 0; // PE32+ only; P5R is x64.
        if (*(int*)(optional + 108) < 1) return 0; // NumberOfRvaAndSizes

        var directories = optional + 112;
        var importRva = *(int*)directories; // IMAGE_DIRECTORY_ENTRY_IMPORT
        var importSize = *(int*)(directories + 4);
        if (importRva <= 0 || importSize <= 0 || (long)importRva + importSize > imageSize) return 0;

        var patched = 0;
        var importEnd = Math.Min((long)imageSize, (long)importRva + importSize);
        for (long descriptorRva = importRva; descriptorRva + 20 <= importEnd; descriptorRva += 20)
        {
            var descriptor = image + (nint)descriptorRva;
            var lookupRva = *(int*)descriptor;
            var nameRva = *(int*)(descriptor + 12);
            var thunkRva = *(int*)(descriptor + 16);
            if (lookupRva == 0 && nameRva == 0 && thunkRva == 0) break;
            // Without the lookup table the IAT holds already-bound addresses, not name RVAs; skip it.
            if (lookupRva <= 0 || thunkRva <= 0 || lookupRva > imageSize - 8 || thunkRva > imageSize - 8)
                continue;

            var importedDll = nameRva > 0 ? ReadAscii(image, imageSize, nameRva) : null;
            for (long i = 0; ; i++)
            {
                var lookupEntryRva = lookupRva + i * 8;
                var iatEntryRva = thunkRva + i * 8;
                if (lookupEntryRva + 8 > imageSize || iatEntryRva + 8 > imageSize) break;

                var value = *(nint*)(image + (nint)lookupEntryRva);
                if (value == 0) break;
                if (((ulong)value >> 63) != 0) continue; // import by ordinal, no name to match
                var hintRva = (long)(uint)(int)value; // low 31 bits are the IMAGE_IMPORT_BY_NAME RVA
                if (hintRva <= 0 || hintRva + 3 > imageSize) continue;
                var function = ReadAscii(image, imageSize, hintRva + 2); // skip the 2-byte hint
                if (function == null || !WantHook(importedDll, function)) continue;
                if (HookIatEntry(image + (nint)iatEntryRva, moduleName, importedDll ?? "?", function)) patched++;
            }
        }
        return patched;
    }

    private static bool WantHook(string? dll, string function)
    {
        foreach (var (dllHint, name) in WindowsImports)
        {
            if (!function.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (DllMatches(dll, dllHint)) return true;
        }
        return false;
    }

    private static bool DllMatches(string? dll, string hint)
    {
        if (string.IsNullOrEmpty(dll)) return false;
        if (hint == "kernel")
            return dll.Contains("kernel32.dll", StringComparison.OrdinalIgnoreCase) ||
                   dll.Contains("kernelbase.dll", StringComparison.OrdinalIgnoreCase);
        return dll.Contains(hint, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HookIatEntry(nint entry, string moduleName, string dll, string function)
    {
        var original = *(nint*)entry;
        if (original == 0) return false;

        lock (Gate)
        {
            if (!PatchedImports.Add((entry, function))) return false;
        }

        var block = CreateStub(original, Kind.Arg, measure: false);
        if (block == 0)
        {
            lock (Gate) PatchedImports.Remove((entry, function));
            return false;
        }

        var slot = new Slot($"win.{moduleName}.{dll}.{function}", 0, Kind.Arg, block,
            _efficient ? 0 : RingEntries);
        lock (Gate) Active.Add(slot);

        // Never write through a failed VirtualProtect. On Wine a non-writable IAT page would otherwise raise an
        // uncatchable access violation and take down the game before the first summary is written.
        if (!VirtualProtect(entry, 8, 0x40, out var old)) // PAGE_EXECUTE_READWRITE
        {
            Log($"  skipped {moduleName}.{function}: VirtualProtect(RWX) failed.");
            lock (Gate) PatchedImports.Remove((entry, function));
            return false;
        }
        *(nint*)entry = block;
        if (!VirtualProtect(entry, 8, old, out _))
            Log($"  warning: could not restore protection for {moduleName}.{function}.");
        FlushInstructionCache(GetCurrentProcess(), entry, 8);
        return true;
    }

    private static string? ReadAscii(nint image, int imageSize, long rva, int max = 256)
    {
        if (rva <= 0 || rva >= imageSize) return null;
        max = (int)Math.Min(max, imageSize - rva);
        if (max <= 1) return null;

        var buffer = new byte[max];
        var address = image + (nint)rva;
        for (var i = 0; i < max; i++)
        {
            var value = Marshal.ReadByte(address, i);
            if (value == 0) return i > 0 ? System.Text.Encoding.ASCII.GetString(buffer, 0, i) : null;
            buffer[i] = value;
        }
        return null; // unterminated string; ignore the import
    }

    private static bool IsExcludedModule(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower == "p5rpc.perfprobe.dll") return true;
        if (lower.Contains("reloaded", StringComparison.Ordinal) ||
            lower.Contains("reloadified", StringComparison.Ordinal)) return true;
        if (lower.StartsWith("system.", StringComparison.Ordinal) ||
            lower.StartsWith("microsoft.", StringComparison.Ordinal) ||
            lower.StartsWith("vcruntime", StringComparison.Ordinal) ||
            lower.StartsWith("msvcp", StringComparison.Ordinal) ||
            lower.StartsWith("concrt", StringComparison.Ordinal) ||
            lower.StartsWith("api-ms-win", StringComparison.Ordinal) ||
            lower.StartsWith("ext-ms-win", StringComparison.Ordinal)) return true;
        return lower is "coreclr.dll" or "clr.dll" or "clrjit.dll" or "hostfxr.dll" or "hostpolicy.dll" or
            "mscorlib.dll" or "netstandard.dll" or "windowsbase.dll" or "presentationcore.dll";
    }

    /// <summary>
    /// Do not rewrite the Windows loader/runtime's own imports. The game still imports Sleep, waits and timers
    /// directly, so the counters keep the interesting calls without touching Wine's core DLLs.
    /// </summary>
    private static bool IsOsModule(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower is "ntdll.dll" or "kernel32.dll" or "kernelbase.dll" or "user32.dll" or "gdi32.dll" or
            "advapi32.dll" or "winmm.dll" or "ole32.dll" or "oleaut32.dll" or "combase.dll" or "rpcrt4.dll" or
            "sechost.dll" or "msvcrt.dll" or "ucrtbase.dll" or "wow64.dll" or "wow64cpu.dll" or "wow64win.dll";
    }

    private static bool IsInterestingModule(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.StartsWith("p5r.exe", StringComparison.Ordinal) ||
               lower.StartsWith("d3d11", StringComparison.Ordinal) ||
               lower.StartsWith("dxgi", StringComparison.Ordinal) ||
               lower.Contains("xinput", StringComparison.Ordinal) ||
               lower.Contains("dinput", StringComparison.Ordinal) ||
               lower.Contains("steam", StringComparison.Ordinal);
    }

    /// <summary>
    /// Games often create the device with D3D11CreateDevice and the swapchain separately through IDXGIFactory.
    /// Hooking factory creation lets us patch its CreateSwapChain once and still report Present/ResizeBuffers.
    /// </summary>
    private static void HookDxgiFactoryCreation()
    {
        var dxgi = LoadLibraryW("dxgi.dll");
        if (dxgi == 0) { Log("dxgi.dll could not be loaded; swapchain Present will not be counted."); return; }

        HookFactory(dxgi, "CreateDXGIFactory", flags: false);
        HookFactory(dxgi, "CreateDXGIFactory1", flags: false);
        HookFactory(dxgi, "CreateDXGIFactory2", flags: true);
    }

    private static void HookFactory(nint dxgi, string name, bool flags)
    {
        var address = GetProcAddress(dxgi, name);
        if (address == 0 || !HookedFunctions.Add(address) || _hooks == null) return;

        try
        {
            if (flags)
            {
                var hook = _hooks.CreateHook<CreateDxgiFactory2Fn>(typeof(Probe), nameof(DxgiFactory2Hook), (long)address);
                if (hook == null) return;
                _originalCreateDxgiFactory2 = Marshal.GetDelegateForFunctionPointer<CreateDxgiFactory2Fn>(
                    (nint)hook.OriginalFunctionAddress);
                KeepAlive.Add(hook);
                hook.Activate();
            }
            else
            {
                var hook = _hooks.CreateHook<CreateDxgiFactoryFn>(typeof(Probe), nameof(DxgiFactoryHook), (long)address);
                if (hook == null) return;
                _originalCreateDxgiFactory = Marshal.GetDelegateForFunctionPointer<CreateDxgiFactoryFn>(
                    (nint)hook.OriginalFunctionAddress);
                KeepAlive.Add(hook);
                hook.Activate();
            }
            Log($"Hooked {name} at 0x{address:X} in {ModuleName(address)}.");
        }
        catch (Exception exception)
        {
            Log($"Could not hook {name} at 0x{address:X}: {exception.Message}");
        }
    }

    [UnmanagedCallersOnly]
    private static int DxgiFactoryHook(nint riid, nint ppFactory)
    {
        var hr = _originalCreateDxgiFactory!(riid, ppFactory);
        if (hr >= 0 && ppFactory != 0) InstallFactoryHooks(*(nint*)ppFactory);
        return hr;
    }

    [UnmanagedCallersOnly]
    private static int DxgiFactory2Hook(uint flags, nint riid, nint ppFactory)
    {
        var hr = _originalCreateDxgiFactory2!(flags, riid, ppFactory);
        if (hr >= 0 && ppFactory != 0) InstallFactoryHooks(*(nint*)ppFactory);
        return hr;
    }

    private static void InstallFactoryHooks(nint factory)
    {
        if (factory == 0 || _hooks == null) return;
        var vtbl = *(nint*)factory;
        if (vtbl == 0) return;

        nint function;
        lock (Gate)
        {
            function = ((nint*)vtbl)[10]; // IDXGIFactory::CreateSwapChain (same slot in Factory1/2)
            if (function == 0 || !HookedFunctions.Add(function)) return;
        }

        try
        {
            Reloaded.Hooks.Definitions.IHook<CreateSwapChainFn>? hook = null;
            CreateSwapChainFn impl = (f, device, desc, ppSwapChain) =>
            {
                var hr = hook!.OriginalFunction(f, device, desc, ppSwapChain);
                if (hr >= 0 && ppSwapChain != 0)
                {
                    var swapChain = *(nint*)ppSwapChain;
                    if (swapChain != 0) InstrumentVtable("swap", swapChain, _efficient ? EfficientSwapSlots : SwapSlots);
                }
                return hr;
            };
            hook = _hooks.CreateHook(impl, (long)function);
            KeepAlive.Add(impl);
            if (hook == null) return;
            KeepAlive.Add(hook);
            hook.Activate();
            Log($"Hooked IDXGIFactory::CreateSwapChain at 0x{function:X} ({ModuleName(function)}).");
        }
        catch (Exception exception)
        {
            Log($"Could not hook IDXGIFactory::CreateSwapChain at 0x{function:X}: {exception.Message}");
        }
    }

    private static void HookCreation(nint module, string name, bool swapChain)
    {
        var address = GetProcAddress(module, name);
        if (address == 0 || !HookedFunctions.Add(address) || _hooks == null) return;

        try
        {
            if (swapChain)
            {
                var hook = _hooks.CreateHook<D3D11CreateDeviceSwapChainFn>(typeof(Probe),
                    nameof(D3D11CreateDeviceAndSwapChainHook), (long)address);
                if (hook == null) { Log($"Could not hook {name} at 0x{address:X}."); return; }
                _originalD3D11CreateDeviceAndSwapChain = Marshal.GetDelegateForFunctionPointer<D3D11CreateDeviceSwapChainFn>(
                    (nint)hook.OriginalFunctionAddress);
                KeepAlive.Add(hook);
                hook.Activate();
            }
            else if (name.Equals("D3D11CoreCreateDevice", StringComparison.OrdinalIgnoreCase))
            {
                var hook = _hooks.CreateHook<D3D11CreateDeviceFn>(typeof(Probe),
                    nameof(D3D11CoreCreateDeviceHook), (long)address);
                if (hook == null) { Log($"Could not hook {name} at 0x{address:X}."); return; }
                _originalD3D11CoreCreateDevice = Marshal.GetDelegateForFunctionPointer<D3D11CreateDeviceFn>(
                    (nint)hook.OriginalFunctionAddress);
                KeepAlive.Add(hook);
                hook.Activate();
            }
            else
            {
                var hook = _hooks.CreateHook<D3D11CreateDeviceFn>(typeof(Probe),
                    nameof(D3D11CreateDeviceHook), (long)address);
                if (hook == null) { Log($"Could not hook {name} at 0x{address:X}."); return; }
                _originalD3D11CreateDevice = Marshal.GetDelegateForFunctionPointer<D3D11CreateDeviceFn>(
                    (nint)hook.OriginalFunctionAddress);
                KeepAlive.Add(hook);
                hook.Activate();
            }

            Log($"Hooked {name} at 0x{address:X} in {ModuleName(address)}.");
        }
        catch (Exception exception)
        {
            Log($"Could not hook {name} at 0x{address:X}: {exception.Message}");
        }
    }

    [UnmanagedCallersOnly]
    private static int D3D11CreateDeviceHook(nint adapter, int driverType, nint software, uint flags,
        nint featureLevels, uint featureLevelCount, uint sdkVersion, nint ppDevice, nint pFeatureLevel,
        nint ppImmediateContext)
    {
        var hr = _originalD3D11CreateDevice!(adapter, driverType, software, flags, featureLevels,
            featureLevelCount, sdkVersion, ppDevice, pFeatureLevel, ppImmediateContext);
        if (hr >= 0) Capture(ppDevice, ppImmediateContext, 0);
        return hr;
    }

    [UnmanagedCallersOnly]
    private static int D3D11CoreCreateDeviceHook(nint adapter, int driverType, nint software, uint flags,
        nint featureLevels, uint featureLevelCount, uint sdkVersion, nint ppDevice, nint pFeatureLevel,
        nint ppImmediateContext)
    {
        var hr = _originalD3D11CoreCreateDevice!(adapter, driverType, software, flags, featureLevels,
            featureLevelCount, sdkVersion, ppDevice, pFeatureLevel, ppImmediateContext);
        if (hr >= 0) Capture(ppDevice, ppImmediateContext, 0);
        return hr;
    }

    [UnmanagedCallersOnly]
    private static int D3D11CreateDeviceAndSwapChainHook(nint adapter, int driverType, nint software, uint flags,
        nint featureLevels, uint featureLevelCount, uint sdkVersion, nint swapChainDesc, nint ppSwapChain,
        nint ppDevice, nint pFeatureLevel, nint ppImmediateContext)
    {
        var hr = _originalD3D11CreateDeviceAndSwapChain!(adapter, driverType, software, flags, featureLevels,
            featureLevelCount, sdkVersion, swapChainDesc, ppSwapChain, ppDevice, pFeatureLevel, ppImmediateContext);
        if (hr >= 0) Capture(ppDevice, ppImmediateContext, ppSwapChain);
        return hr;
    }

    private static void Capture(nint ppDevice, nint ppContext, nint ppSwapChain)
    {
        if (ppDevice != 0)
        {
            var device = *(nint*)ppDevice;
            if (device != 0) InstallDeviceHooks(device);
        }
        if (ppContext != 0)
        {
            var context = *(nint*)ppContext;
            if (context != 0) InstrumentVtable("ctx", context, _efficient ? EfficientContextSlots : ContextSlots);
        }
        if (ppSwapChain != 0)
        {
            var swapChain = *(nint*)ppSwapChain;
            if (swapChain != 0) InstrumentVtable("swap", swapChain, _efficient ? EfficientSwapSlots : SwapSlots);
        }
    }

    /// <summary>Hooks CreateDeferredContext so contexts made later (the ones the game renders with) get instrumented too.</summary>
    private static void InstallDeviceHooks(nint device)
    {
        if (_hooks == null) return;
        var vtbl = *(nint*)device;
        if (vtbl == 0) return;

        nint function;
        bool hookCreation;
        lock (Gate)
        {
            function = ((nint*)vtbl)[27]; // ID3D11Device::CreateDeferredContext
            hookCreation = function != 0 && HookedFunctions.Add(function);
        }

        if (hookCreation)
        {
            try
            {
                Reloaded.Hooks.Definitions.IHook<CreateDeferredContextFn>? hook = null;
                CreateDeferredContextFn impl = (dev, flags, pp) =>
                {
                    var hr = hook!.OriginalFunction(dev, flags, pp);
                    if (hr >= 0 && pp != 0)
                    {
                        var context = *(nint*)pp;
                        if (context != 0) InstallContextHooks(context);
                    }
                    return hr;
                };
                hook = _hooks.CreateHook(impl, (long)function);
                KeepAlive.Add(impl);
                if (hook == null) Log($"Could not hook CreateDeferredContext at 0x{function:X}.");
                else
                {
                    KeepAlive.Add(hook);
                    hook.Activate();
                    Log($"Hooked CreateDeferredContext at 0x{function:X} ({ModuleName(function)}).");
                }
            }
            catch (Exception exception)
            {
                Log($"Could not hook CreateDeferredContext at 0x{function:X}: {exception.Message}");
            }
        }

        // Do this last: it replaces vtable slot 27 with the counter stub, so the original function address must be read first.
        InstrumentVtable("dev", device, _efficient ? EfficientDeviceSlots : DeviceSlots);
    }

    private static void InstallContextHooks(nint context) => InstrumentVtable("ctx", context, _efficient ? EfficientContextSlots : ContextSlots);

    private static void InstrumentVtable(string prefix, nint instance, SlotSpec[] specs)
    {
        var vtbl = *(nint*)instance;
        if (vtbl == 0) return;
        lock (Gate)
        {
            if (PatchedVtables.Contains((prefix, vtbl))) return;
            PatchedVtables.Add((prefix, vtbl));
        }

        var names = new List<string>();
        foreach (var spec in specs)
        {
            if (!_detailed && !spec.Default) continue;
            var original = ((nint*)vtbl)[spec.Slot];
            if (original == 0) continue;

            var block = CreateStub(original, spec.Kind, _measureTime);
            if (block == 0) { Log($"Could not allocate a stub for {spec.Name}."); continue; }

            var capacity = _efficient
                ? spec.Kind == Kind.Present ? EfficientRingEntries : 0
                : RingEntries;
            var slot = new Slot($"{prefix}.{spec.Name}", spec.Slot, spec.Kind, block, capacity);
            lock (Gate) Active.Add(slot);

            var entry = vtbl + spec.Slot * 8;
            if (!VirtualProtect(entry, 8, 0x40, out var old))
            {
                Log($"  skipped {prefix}.{spec.Name}: VirtualProtect(RWX) failed.");
                continue;
            }
            *(nint*)entry = block;
            if (!VirtualProtect(entry, 8, old, out _))
                Log($"  warning: could not restore protection for {prefix}.{spec.Name}.");
            FlushInstructionCache(GetCurrentProcess(), entry, 8);
            names.Add($"{spec.Name}({spec.Slot})");
        }

        Log($"Context/interface 0x{instance:X}, vtable 0x{vtbl:X}: instrumented {string.Join(", ", names)}.");
        RefreshModules();
    }

    /// <summary>
    /// Count-only mode emits a minimal tail-call stub: count, optionally record return address/TSC, primitive total, jmp.
    ///
    /// Timed mode emits a call/ret wrapper. It saves the game return address and nonvolatile registers, records an
    /// RDTSC timestamp, calls the original function with the caller's stack layout unchanged, takes a second RDTSC,
    /// adds the delta to a per-slot cycle counter, restores RAX/stack/registers and returns. Stack arguments stay at
    /// their original absolute addresses: entering with RSP=E, the wrapper adds 8 before the call so the original sees
    /// the same frame a normal call would have produced. No managed code runs per call in either mode.
    /// </summary>
    private static nint CreateStub(nint original, Kind kind, bool measure)
    {
        // Efficient stubs never touch the caller rings; only Present needs the small frame-time ring.
        var size = _efficient
            ? kind == Kind.Present ? EfficientPresentBlockSize : EfficientBlockSize
            : BlockSize;
        var block = VirtualAlloc(0, (nuint)size, 0x3000, 0x40); // MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE
        if (block == 0) return 0;

        var code = EmitStub((long)block, original, kind, measure, rings: !_efficient,
            ringEntries: _efficient ? EfficientRingEntries : RingEntries);
        Marshal.Copy(code, 0, block, code.Length);
        *(nint*)(block + OriginalOffset) = original;
        FlushInstructionCache(GetCurrentProcess(), block, (nuint)code.Length);
        return block;
    }

    /// <summary>Exposed only so the generated bytes can be disassembled and executed in an offline check.</summary>
    public static byte[] DebugStub(long block, bool draw, bool instanced, bool measure, bool efficient = false) =>
        EmitStub(block, unchecked((nint)0x2222222222222222L),
            instanced ? Kind.DrawInstanced : draw ? Kind.Draw : Kind.Counter, measure, rings: !efficient,
            ringEntries: efficient ? EfficientRingEntries : RingEntries);

    /// <summary>Exposes the Windows-call IAT stub bytes for offline disassembly.</summary>
    public static byte[] DebugArgStub(long block, bool efficient = false) =>
        EmitStub(block, unchecked((nint)0x2222222222222222L), Kind.Arg, measure: false, rings: !efficient,
            ringEntries: efficient ? EfficientRingEntries : RingEntries);

    /// <summary>Exposes the Present stub bytes for offline disassembly.</summary>
    public static byte[] DebugPresentStub(long block, bool efficient) =>
        EmitStub(block, unchecked((nint)0x2222222222222222L), Kind.Present, measure: false, rings: !efficient,
            ringEntries: efficient ? EfficientRingEntries : RingEntries);

    private static byte[] EmitStub(long block, nint original, Kind kind, bool measure, bool rings = true,
        int ringEntries = RingEntries)
    {
        var efficient = !rings && !measure;
        var ringMask = ringEntries - 1;
        var code = new List<byte>(CodeSize);
        void E(params byte[] bytes) => code.AddRange(bytes);
        void I32(int value) => code.AddRange(BitConverter.GetBytes(value));
        void I64(long value) => code.AddRange(BitConverter.GetBytes(value));

        if (measure)
        {
            E(0x53);                                            // push rbx       (save nonvolatile)
            E(0x41, 0x54);                                      // push r12       (save nonvolatile)
            E(0x50);                                            // push rax
            E(0x52);                                            // push rdx
            E(0x4C, 0x8B, 0x5C, 0x24, 0x20);                    // mov r11,[rsp+32] game return address
        }
        else if (rings)
        {
            E(0x4C, 0x8B, 0x1C, 0x24);                          // mov r11,[rsp] game return address
        }

        if (efficient && kind != Kind.Present)
        {
            // No ring write, so the returned index is dead. One lock inc is enough.
            E(0xF0, 0x48, 0xFF, 0x05); I32(IdxOffset - (code.Count + 4)); // lock inc [rip+idx]
        }
        else
        {
            E(0x41, 0xBA, 0x01, 0x00, 0x00, 0x00);              // mov r10d,1
            E(0xF0, 0x4C, 0x0F, 0xC1, 0x15); I32(IdxOffset - (code.Count + 4)); // lock xadd [rip+idx],r10
            E(0x49, 0x81, 0xE2); I32(ringMask);                 // and r10,mask
        }

        if (measure)
        {
            E(0x48, 0xB8); I64(block + RingOffset);             // mov rax,ring
            E(0x4E, 0x89, 0x1C, 0xD0);                          // mov [rax+r10*8],r11
        }
        else if (rings)
        {
            E(0x48, 0x8D, 0x05); I32(RingOffset - (code.Count + 4)); // lea rax,[rip+ring]
            E(0x4E, 0x89, 0x1C, 0xD0);                          // mov [rax+r10*8],r11
            if (kind == Kind.Arg)
            {
                E(0x49, 0x89, 0xCB);                            // mov r11,rcx (first argument)
                E(0x48, 0x8D, 0x05); I32(ArgOffset - (code.Count + 4)); // lea rax,[rip+arg ring]
                E(0x4E, 0x89, 0x1C, 0xD0);                      // mov [rax+r10*8],r11
            }
            else if (kind == Kind.Present)
            {
                // Special K style: a raw TSC timestamp per Present, stored in the unused argument ring.
                // No call, no stack change and no managed work on the render thread.
                E(0x52);                                        // push rdx (Present argument)
                E(0x50);                                        // push rax
                E(0x0F, 0x31);                                  // rdtsc
                E(0x48, 0xC1, 0xE2, 0x20);                      // shl rdx,32
                E(0x48, 0x09, 0xD0);                            // or rax,rdx (TSC in rax)
                E(0x49, 0x89, 0xC3);                            // mov r11,rax
                E(0x58);                                        // pop rax
                E(0x5A);                                        // pop rdx
                E(0x48, 0x8D, 0x05); I32(ArgOffset - (code.Count + 4)); // lea rax,[rip+arg ring]
                E(0x4E, 0x89, 0x1C, 0xD0);                      // mov [rax+r10*8],r11
            }
        }
        else if (kind == Kind.Present)
        {
            // Efficient mode skips return-address rings, but still needs one TSC stamp per Present.
            // Save the SyncInterval argument in r11, restore it before the tail jump, and use r11 as the ring base.
            E(0x49, 0x89, 0xD3);                                // mov r11,rdx
            E(0x0F, 0x31);                                      // rdtsc
            E(0x48, 0xC1, 0xE2, 0x20);                          // shl rdx,32
            E(0x48, 0x09, 0xD0);                                // or rax,rdx (TSC in rax)
            E(0x4C, 0x89, 0xDA);                                // mov rdx,r11 (restore Present argument)
            E(0x4C, 0x8D, 0x1D); I32(ArgOffset - (code.Count + 4)); // lea r11,[rip+arg ring]
            E(0x4B, 0x89, 0x04, 0xD3);                          // mov [r11+r10*8],rax
        }

        if (measure)
        {
            E(0x5A);                                            // pop rdx
            E(0x58);                                            // pop rax
        }

        if (kind is Kind.Draw or Kind.DrawInstanced)
        {
            if (kind == Kind.DrawInstanced)
            {
                E(0x4C, 0x8B, 0xD2);                            // mov r10,rdx
                E(0x4D, 0x0F, 0xAF, 0xD0);                      // imul r10,r8
            }

            if (efficient)
            {
                // RIP-relative avoids clobbering RAX and removes a mov from the per-draw hot path.
                E(kind == Kind.DrawInstanced
                    ? new byte[] { 0xF0, 0x4C, 0x01, 0x15 }    // lock add [rip+total],r10
                    : new byte[] { 0xF0, 0x48, 0x01, 0x15 }); // lock add [rip+total],rdx
                I32(TotalOffset - (code.Count + 4));
            }
            else
            {
                E(0x48, 0xB8); I64(block + TotalOffset);        // mov rax,total
                E(kind == Kind.DrawInstanced
                    ? new byte[] { 0xF0, 0x4C, 0x01, 0x10 }    // lock add [rax],r10
                    : new byte[] { 0xF0, 0x48, 0x01, 0x10 }); // lock add [rax],rdx
            }
        }

        if (measure)
        {
            E(0x50);                                            // push rax
            E(0x52);                                            // push rdx
            E(0x0F, 0x31);                                      // rdtsc
            E(0x48, 0xC1, 0xE2, 0x20);                          // shl rdx,32
            E(0x48, 0x09, 0xD0);                                // or rdx,rax
            E(0x49, 0x89, 0xC4);                                // mov r12,rax  (entry TSC)
            E(0x5A);                                            // pop rdx
            E(0x58);                                            // pop rax
            E(0x48, 0x8B, 0x5C, 0x24, 0x10);                    // mov rbx,[rsp+16] game return address
            E(0x48, 0x83, 0xC4, 0x18);                          // add rsp,24    (caller alignment)
            E(0xFF, 0x15); I32(OriginalOffset - (code.Count + 4)); // call [rip+original]
            E(0x50);                                            // push rax      (save result)
            E(0x0F, 0x31);                                      // rdtsc
            E(0x48, 0xC1, 0xE2, 0x20);                          // shl rdx,32
            E(0x48, 0x09, 0xD0);                                // or rdx,rax
            E(0x49, 0x89, 0xC3);                                // mov r11,rax
            E(0x4D, 0x29, 0xE3);                                // sub r11,r12  (cycles spent in call)
            E(0x48, 0xB8); I64(block + CyclesOffset);            // mov rax,cycles
            E(0xF0, 0x4C, 0x01, 0x18);                          // lock add [rax],r11
            E(0x58);                                            // pop rax       (restore result)
            E(0x48, 0x83, 0xEC, 0x08);                          // sub rsp,8     (back to the original callee frame)
            E(0x48, 0x89, 0x1C, 0x24);                          // mov [rsp],rbx restore game return address
            E(0x4C, 0x8B, 0x64, 0x24, 0xF0);                    // mov r12,[rsp-16]
            E(0x48, 0x8B, 0x5C, 0x24, 0xF8);                    // mov rbx,[rsp-8]
            E(0xC3);                                            // ret
        }
        else
        {
            E(0xFF, 0x25); I32(OriginalOffset - (code.Count + 4)); // jmp [rip+original]
        }

        while (code.Count < CodeSize) E(0x90);
        return code.ToArray();
    }

    /// <summary>Writes one interval of Windows/D3D call counts, FPS, process CPU and the busiest callers.</summary>
    public static void LogSummary(int top)
    {
        if (!_installed) return;
        if (!_efficient) RefreshModules();

        var added = _profileWindows ? InstallWindowsApiHooks() : 0;
        if (added > 0) Log($"Windows API IAT counters: {added} additional imports found.");

        // Build the whole interval in memory and append it once. The previous version called File.AppendAllText
        // for every line (~17k opens/closes per two-minute run), which showed up as ~60% of one core in .NET
        // thread-pool CPU under Wine.
        var lines = new List<string>();
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        void Add(string message) => lines.Add($"{stamp} {message}");

        try
        {
            var wallNow = Stopwatch.GetTimestamp();
            var wallSeconds = (wallNow - _prevWall) / (double)Stopwatch.Frequency;
            _prevWall = wallNow;
            GetProcessTimes(GetCurrentProcess(), out _, out _, out var kernelTime, out var userTime);
            var kernel = ToUInt64(kernelTime);
            var user = ToUInt64(userTime);
            var cpuSeconds = (kernel - _prevKernel + user - _prevUser) / 10_000_000.0;
            _prevKernel = kernel;
            _prevUser = user;

            Add("--- P5R performance probe ---");
            List<Slot> slots;
            lock (Gate) slots = Active.ToList();
            if (slots.Count == 0)
            {
                Add("No interfaces were instrumented. The game may not have created its device yet, or creation " +
                    "went through a function the probe could not hook.");
                return;
            }

            var countDeltas = new Dictionary<Slot, long>();
            var totalDeltas = new Dictionary<Slot, long>();
            var cycleDeltas = new Dictionary<Slot, long>();
            foreach (var slot in slots)
            {
                countDeltas[slot] = Delta(slot, PreviousCounts);
                totalDeltas[slot] = DeltaTotal(slot);
                cycleDeltas[slot] = DeltaCycles(slot);
            }

            var present = slots.FirstOrDefault(x => x.Name == "swap.Present");
            var presentDelta = present != null && countDeltas.TryGetValue(present, out var pd) ? pd : 0;
            var frames = present != null ? Math.Max(1, presentDelta) : 0;
            var apiMs = cycleDeltas.Values.Sum() / Math.Max(1, _cyclesPerSecond) * 1000.0;
            Add($"interval {wallSeconds * 1000:0} ms, CPU {cpuSeconds / Math.Max(wallSeconds, 0.001) * 100:0.0}% of one core, " +
                $"API CPU {apiMs:0.0} ms, Present {presentDelta:N0} ({presentDelta / Math.Max(wallSeconds, 0.001):0.0} FPS)");
            LogFrameTimes(present, presentDelta, Add);

            var windows = slots.Where(x => x.Name.StartsWith("win.", StringComparison.Ordinal)).ToList();
            if (windows.Count > 0)
            {
                Add("--- Windows API calls (native IAT counters) ---");
                foreach (var slot in windows.Where(x => x.Count > 0 || countDeltas[x] > 0)
                                            .OrderByDescending(x => countDeltas[x]))
                {
                    var delta = countDeltas[slot];
                    Add(FormatSlot(slot, delta, 0, cycleDeltas[slot], frames));
                    if (delta == 0) continue;
                    if (_efficient) continue;
                    if (slot.Kind == Kind.Arg) LogTopArgs(slot, delta, Math.Min(8, Math.Max(1, top)), Add);
                    LogTopCallers(slot, top, delta, Add);
                }
            }

            var graphics = slots.Where(x => !x.Name.StartsWith("win.", StringComparison.Ordinal)).ToList();
            if (graphics.Count > 0)
            {
                Add("--- D3D11/DXGI calls ---");
                foreach (var slot in graphics)
                {
                    var delta = countDeltas[slot];
                    Add(FormatSlot(slot, delta, totalDeltas[slot], cycleDeltas[slot], frames));
                    if (!_efficient && delta > 0 && (slot.Kind is Kind.Upload or Kind.Draw or Kind.DrawInstanced))
                        LogTopCallers(slot, top, delta, Add);
                }
            }

            if (_measureTime)
            {
                var topCost = cycleDeltas.Where(x => x.Value > 0)
                    .OrderByDescending(x => x.Value).Take(8)
                    .Select(x => $"{x.Key.Name} {x.Value / _cyclesPerSecond * 1000.0:0.00} ms");
                Add("  top API CPU cost this interval: " + string.Join(", ", topCost));
            }
        }
        finally
        {
            if (lines.Count > 0) Log(string.Join(Environment.NewLine, lines));
        }
    }

    /// <summary>
    /// Special K style frame-pacing snapshot: percentiles from the raw TSC stamps recorded by the Present stub.
    /// Sorting happens once per summary on the timer thread, never on the render thread.
    /// </summary>
    private static void LogFrameTimes(Slot? present, long presentDelta, Action<string> add)
    {
        if (present == null || present.Capacity <= 0 || presentDelta <= 1 || _cyclesPerSecond <= 0) return;
        var count = (int)Math.Min(presentDelta, present.Capacity);
        var stamps = new long[count];
        CopyRing(present.ArgRing, (int)((present.Count - count) & (present.Capacity - 1)), stamps, present.Capacity);

        var deltas = new List<double>();
        for (var i = 1; i < count; i++)
        {
            var cycles = stamps[i] - stamps[i - 1];
            if (cycles <= 0 || cycles > _cyclesPerSecond * 2) continue; // drop wrap, stalls and clock changes
            deltas.Add(cycles / _cyclesPerSecond * 1000.0);
        }
        if (deltas.Count == 0) return;

        deltas.Sort();
        double At(double q) => deltas[Math.Min(deltas.Count - 1, (int)(q * (deltas.Count - 1)))];
        add($"frame time: avg {deltas.Average():0.00} ms, p50 {At(0.50):0.00}, " +
            $"p95 {At(0.95):0.00}, p99 {At(0.99):0.00}, max {deltas[^1]:0.00} ({deltas.Count} frames)");
    }

    private static string FormatSlot(Slot slot, long delta, long primitives, long cycles, long frames)
    {
        var line = $"{slot.Name} (slot {slot.Index}): {slot.Count:N0} total, {delta:N0} this interval";
        if (frames > 0) line += $", {delta / (double)frames:0.00}/frame";
        if (slot.Kind is Kind.Draw or Kind.DrawInstanced)
            line += $", {primitives:N0} primitives this interval";
        if (_measureTime && delta > 0)
            line += $", CPU {cycles / _cyclesPerSecond * 1000.0:0.000} ms " +
                    $"({cycles / _cyclesPerSecond * 1_000_000.0 / delta:0.000} us/call)";
        return line;
    }

    private static void LogTopArgs(Slot slot, long delta, int top, Action<string> add)
    {
        if (slot.Capacity <= 0) return;
        // Zero is a meaningful argument (Sleep(0), SleepEx(0), NtDelayExecution(FALSE)), so read only the entries
        // written in this interval instead of filtering the untouched tail of the ring.
        var count = (int)Math.Min(delta, slot.Capacity);
        if (count <= 0) return;
        var arguments = new long[count];
        CopyRing(slot.ArgRing, (int)((slot.Count - count) & (slot.Capacity - 1)), arguments, slot.Capacity);
        var counts = new Dictionary<long, int>();
        foreach (var value in arguments)
        {
            counts.TryGetValue(value, out var n);
            counts[value] = n + 1;
        }
        var topArgs = counts.OrderByDescending(x => x.Value).Take(Math.Max(1, top))
            .Select(x => $"0x{x.Key:X} ({x.Value:N0})");
        add($"    top first arguments (RCX): {string.Join(", ", topArgs)}");
    }

    private static void LogTopCallers(Slot slot, int top, long delta, Action<string> add)
    {
        if (slot.Capacity <= 0) return;
        var count = (int)Math.Min(Math.Max(1, delta), slot.Capacity);
        var addresses = new nint[count];
        CopyRing(slot.Ring, (int)((slot.Count - count) & (slot.Capacity - 1)), addresses, slot.Capacity);
        var counts = new Dictionary<nint, int>();
        var sampled = 0;
        foreach (var address in addresses)
        {
            if (address == 0) continue;
            counts.TryGetValue(address, out var n);
            counts[address] = n + 1;
            sampled++;
        }
        if (sampled == 0) return;
        add($"    top return addresses from the last {sampled:N0} samples:");
        foreach (var (address, n) in counts.OrderByDescending(x => x.Value).Take(Math.Max(1, top)))
            add($"      {n,7:N0}  {Describe(address)}");
    }

    /// <summary>Copies a ring slice without reading past the allocation when the tail wraps.</summary>
    private static void CopyRing(nint ring, int start, long[] destination, int capacity)
    {
        if (capacity <= 0 || destination.Length == 0) return;
        start &= capacity - 1;
        var first = Math.Min(destination.Length, capacity - start);
        Marshal.Copy(ring + start * 8, destination, 0, first);
        if (first < destination.Length)
            Marshal.Copy(ring, destination, first, destination.Length - first);
    }

    private static void CopyRing(nint ring, int start, nint[] destination, int capacity)
    {
        if (capacity <= 0 || destination.Length == 0) return;
        start &= capacity - 1;
        var first = Math.Min(destination.Length, capacity - start);
        Marshal.Copy(ring + start * 8, destination, 0, first);
        if (first < destination.Length)
            Marshal.Copy(ring, destination, first, destination.Length - first);
    }

    private static long Delta(Slot? slot, Dictionary<Slot, long> previous)
    {
        if (slot == null) return 0;
        var now = slot.Count;
        if (!previous.ContainsKey(slot)) { previous[slot] = now; return 0; } // first interval has no baseline
        var delta = now - previous[slot];
        previous[slot] = now;
        return Math.Max(0, delta);
    }

    private static long DeltaTotal(Slot slot)
    {
        var now = slot.Total;
        if (!PreviousTotals.ContainsKey(slot)) { PreviousTotals[slot] = now; return 0; }
        var delta = now - PreviousTotals[slot];
        PreviousTotals[slot] = now;
        return Math.Max(0, delta);
    }

    private static long DeltaCycles(Slot slot)
    {
        var now = slot.Cycles;
        if (!PreviousCycles.ContainsKey(slot)) { PreviousCycles[slot] = now; return 0; }
        var delta = now - PreviousCycles[slot];
        PreviousCycles[slot] = now;
        return Math.Max(0, delta);
    }

    private static void RefreshModules()
    {
        var list = new List<ModuleInfo>();
        foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
        {
            try { list.Add(new ModuleInfo(module.BaseAddress, module.ModuleMemorySize, module.ModuleName)); }
            catch { /* ignore modules that unload during enumeration */ }
        }
        _modules = list.OrderByDescending(x => x.Base).ToArray();
    }

    private static string Describe(nint address)
    {
        var text = $"0x{address:X}";
        foreach (var module in _modules)
        {
            if (address < module.Base || address >= module.Base + module.Size) continue;
            text = $"{module.Name}+0x{address - module.Base:X} ({text})";
            break;
        }

        // The performance fix places its routines around 0x158140000, far outside any loaded module.
        if ((ulong)address is >= 0x158000000 and < 0x159000000) text += " [performance fix routine]";
        return text;
    }

    private static string ModuleName(nint address)
    {
        foreach (var module in _modules)
            if (address >= module.Base && address < module.Base + module.Size) return module.Name;
        return "unknown module";
    }

    private static void Log(string message) => _log?.Invoke(message);
    private static ulong ToUInt64(FILETIME ft) => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate ulong RdtscFn();

    /// <summary>Calibrates RDTSC ticks against Stopwatch once, at install time, so cycle deltas can be logged as ms/us.</summary>
    private static void CalibrateCycles()
    {
        try
        {
            var stub = VirtualAlloc(0, 16, 0x3000, 0x40);
            if (stub == 0) return;
            byte[] code = [0x0F, 0x31, 0x48, 0xC1, 0xE2, 0x20, 0x48, 0x09, 0xD0, 0xC3]; // rdtsc; shl rdx,32; or rax,rdx; ret
            Marshal.Copy(code, 0, stub, code.Length);
            FlushInstructionCache(GetCurrentProcess(), stub, (nuint)code.Length);
            var rdtsc = Marshal.GetDelegateForFunctionPointer<RdtscFn>(stub);

            var w0 = Stopwatch.GetTimestamp();
            var c0 = rdtsc();
            Thread.Sleep(50);
            var w1 = Stopwatch.GetTimestamp();
            var c1 = rdtsc();
            var seconds = (w1 - w0) / (double)Stopwatch.Frequency;
            if (seconds > 0 && c1 > c0) _cyclesPerSecond = (c1 - c0) / seconds;
            Log($"RDTSC calibrated: {_cyclesPerSecond / 1_000_000.0:0.0} MHz.");
        }
        catch (Exception e)
        {
            Log($"RDTSC calibration failed: {e.Message}");
        }
    }

    private sealed record ModuleInfo(nint Base, int Size, string Name);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int D3D11CreateDeviceFn(nint adapter, int driverType, nint software, uint flags,
        nint featureLevels, uint featureLevelCount, uint sdkVersion, nint a, nint b, nint c);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int D3D11CreateDeviceSwapChainFn(nint adapter, int driverType, nint software, uint flags,
        nint featureLevels, uint featureLevelCount, uint sdkVersion, nint swapChainDesc,
        nint ppSwapChain, nint ppDevice, nint pFeatureLevel, nint ppImmediateContext);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateDeferredContextFn(nint device, uint flags, nint ppContext);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateDxgiFactoryFn(nint riid, nint ppFactory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateDxgiFactory2Fn(uint flags, nint riid, nint ppFactory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateSwapChainFn(nint factory, nint device, nint desc, nint ppSwapChain);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadLibraryW(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern nint GetProcAddress(nint module, string name);

    [DllImport("kernel32.dll")]
    private static extern bool GetModuleHandleEx(uint flags, nint address, out nint module);

    [DllImport("kernel32.dll")]
    private static extern bool VirtualProtect(nint address, nuint size, uint protect, out uint old);

    [DllImport("kernel32.dll")]
    private static extern nint VirtualAlloc(nint address, nuint size, uint type, uint protect);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool FlushInstructionCache(nint process, nint address, nuint size);

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessTimes(nint process, out FILETIME creation, out FILETIME exit,
        out FILETIME kernel, out FILETIME user);
}
