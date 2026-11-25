# p5rpc.perfprobe

Diagnostic-only Reloaded-II mod for P5R. It counts:

- **Windows API calls** made through module IATs: `Sleep`, `SleepEx`, `SleepConditionVariableCS/SRW`,
  `SwitchToThread`, `WaitForSingleObject/MultipleObjects`, `NtDelayExecution`, `NtWaitFor*`, `NtYieldExecution`,
  `GetTickCount*`, `QueryPerformanceCounter`, `timeBeginPeriod/timeEndPeriod`, `timeGetTime`,
  `PeekMessage*`, `GetMessageW`, `MsgWaitForMultipleObjectsEx`, and affinity calls.
- **D3D11/DXGI calls** made through vtables: uploads, draws, state changes, copies, clears, resource creation,
  Present and swapchain calls.

It reports FPS, frame-time percentiles and process CPU use. In efficient mode the hot path is just a native leaf stub
that increments a locked counter (and stamps `Present` with `rdtsc`), then jumps to the original function: no
caller-ring writes, no managed code, no allocation and no logging per call. Full mode adds return-address/argument
rings for offline caller attribution. Per-call CPU timing remains disabled because the timed wrapper is not
SEH-unwind safe in P5R.

Use the repository's thread CSV sampler or Tracy for per-thread CPU timelines and stack attribution. The probe itself
only writes counters; it does not add a per-thread sampler to the game process.

## Log

`perfprobe-log.txt` in the mod folder is cleared at game start. Each interval writes:

- interval length, process CPU use, Present count and FPS
- **Windows API calls**: counts, calls/frame, top RCX values and top return addresses
- **D3D11/DXGI calls**: counts, calls/frame, primitive totals for draws, top return addresses for uploads/draws

The `win.<module>.<dll>.<function>` names identify Windows counters; `ctx.`, `dev.` and `swap.` identify graphics
counters. Return addresses are resolved against loaded modules, with P5R.exe offsets and performancefix routines
labelled.

## Config

- **Efficient mode** — default on. Minimal native counters plus frame-time percentiles, no caller-ring recording or IAT scan.
- **Log interval (seconds)** — default 1 s.
- **Top callers to show** — default 8; ignored in efficient mode.
- **Profile Windows API calls** — default on.
- **Include all non-runtime modules** — default on. Off limits counters to game/D3D11/DXGI/input/Steam modules;
  on patches every non-runtime, non-OS module except the probe, Reloaded and .NET assemblies.
- **Detailed mode** — off by default. Curated mode covers ~60 render-work slots in the device, immediate context
  and swapchain. Detailed mode adds the remaining useful slots.
- **Measure per-call CPU time** — forced off; retained only for documentation.

## Compatibility

Safe alongside `p5rpc.performancefix`: the probe counts API entry points and labels its code addresses, while
performancefix rewrites the game's upload call sites.

The mod hooks `D3D11CreateDevice`, `D3D11CoreCreateDevice`, `D3D11CreateDeviceAndSwapChain`,
`CreateDXGIFactory{,1,2}` and `IDXGIFactory::CreateSwapChain` so it can instrument deferred contexts and swapchains
created later. Windows API counters are installed by patching import tables with native stubs; later-loaded modules
are picked up by the background logger.

Version 0.4.1 makes the IAT scanner bounds-safe, skips OS/loader modules, and refuses to write through a failed
`VirtualProtect`. Version 0.4.2 batches each interval into one file append (the old code opened the log ~17k times in
two minutes) and adds Special K style Present frame-time percentiles from native TSC stamps. Version 0.4.3 defaults
to efficient mode: only Present, draws, maps, uploads and two creation counters are instrumented; no caller rings
or IAT scan run. Version 0.4.4 removes even the return-address write from those efficient-mode stubs. Version 0.4.5
preserves the `Present` arguments around `rdtsc` (the TSC stub must not clobber `rdx`/`SyncInterval`). Version 0.4.6
minimises the hot path further: non-Present counters use one `lock inc`, draw primitives use RIP-relative `lock add`,
the Present stub restores `rdx` without stack pushes, and the frame-time ring is 256 entries instead of 16384 around
a much smaller native allocation. Inspired by Ultra and Special K, interpretation is offline: the game threads only
bump counters and stamp Present; sorting, percentiles and formatting happen on the background timer thread.
