using p5rpc.perfprobe.Configuration;
using p5rpc.perfprobe.Template;
using Reloaded.Mod.Interfaces;

namespace p5rpc.perfprobe;

/// <summary>
/// Diagnostic-only Reloaded mod. Watches the D3D11 contexts P5R.exe actually uses and reports which addresses
/// submit uploads and draws. See <see cref="Probe"/> for how the counting works.
/// </summary>
public unsafe class Mod : ModBase
{
    private readonly IModLoader _modLoader;
    private readonly ILogger _logger;
    private readonly IModConfig _modConfig;
    private Config _configuration;
    private Timer? _timer;
    private string? _logPath;

    public Mod(ModContext context)
    {
        _modLoader = context.ModLoader;
        _logger = context.Logger;
        _modConfig = context.ModConfig;
        _configuration = context.Configuration;

        Log("P5R Performance Probe 0.4.7 starting.");
        Probe.Install(context.Hooks, Log, _configuration.DetailedMode, _configuration.MeasureCallTime,
            _configuration.ProfileWindowsCalls, _configuration.ProfileAllModules, _configuration.EfficientMode);
        StartTimer();
    }

    private void StartTimer()
    {
        var seconds = Math.Clamp(_configuration.LogEverySeconds, 1, 3600);
        _timer?.Dispose();
        _timer = new Timer(_ => Probe.LogSummary(_configuration.TopCallers), null, seconds * 1000, seconds * 1000);
        Log($"Summary interval: {seconds} s.");
    }

    private void Log(string message)
    {
        _logger.WriteLine($"[{_modConfig.ModId}] {message}");
        try
        {
            if (_logPath == null)
            {
                var directory = _modLoader.GetDirectoryForModId(_modConfig.ModId);
                if (string.IsNullOrEmpty(directory)) return;
                _logPath = Path.Combine(directory, "perfprobe-log.txt");
                File.WriteAllText(_logPath, $"P5R Performance Probe, session started {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");
            }

            // Summary blocks already carry per-line HH:mm:ss stamps. One append per block instead of one
            // append per line matters under Wine: File.AppendAllText opens/closes the file every time.
            var text = message.Contains('\n') ? message : $"{DateTime.Now:HH:mm:ss} {message}";
            File.AppendAllText(_logPath, text + Environment.NewLine);
        }
        catch
        {
            // The Reloaded console still has it.
        }
    }

    public override void ConfigurationUpdated(Config configuration)
    {
        _configuration = configuration;
        Log("Configuration updated (slot changes need a game restart).");
        StartTimer();
    }

    public override bool CanSuspend() => false;
    public override bool CanUnload() => false;

#pragma warning disable CS8618 // The template creates an instance before StartEx supplies the real context.
    public Mod() { }
#pragma warning restore CS8618
}
