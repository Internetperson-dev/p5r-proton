using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using p5rpc.perfprobe.Template.Configuration;

namespace p5rpc.perfprobe.Configuration;

public class Config : Configurable<Config>
{
    [Category("01. Probe")]
    [DisplayName("Log interval (seconds)")]
    [Description("How often the probe writes a summary to perfprobe-log.txt. Applies straight away. Default 1 s.")]
    [DefaultValue(1)]
    [Display(Order = 0)]
    public int LogEverySeconds { get; set; } = 1;

    [Category("01. Probe")]
    [DisplayName("Efficient mode")]
    [Description("Default on. Only a minimal native counter set (Present, draws, maps, uploads) plus frame-time percentiles; " +
                 "no caller/argument ring recording and no IAT scan. Turn off for full per-call caller diagnostics.")]
    [DefaultValue(true)]
    [Display(Order = 1)]
    public bool EfficientMode { get; set; } = true;

    [Category("01. Probe")]
    [DisplayName("Top callers to show")]
    [Description("How many return addresses to print for upload and draw calls. Applies straight away. Ignored in efficient mode.")]
    [DefaultValue(8)]
    [Display(Order = 2)]
    public int TopCallers { get; set; } = 8;

    [Category("02. Detail (restart required)")]
    [DisplayName("Measure per-call CPU time [disabled]")]
    [Description("Currently disabled: the timed native wrapper is not SEH-unwind safe in P5R. " +
                 "The probe always uses the proven count-only leaf stubs.")]
    [DefaultValue(false)]
    [Display(Order = 2)]
    public bool MeasureCallTime { get; set; } = false;

    [Category("02. Detail (restart required)")]
    [DisplayName("Detailed mode")]
    [Description("Off = curated set of ~60 D3D11 context/device/swapchain calls covering virtually all render work. " +
                 "On = also count the remaining useful vtable slots. The hot path stays native either way.")]
    [DefaultValue(false)]
    [Display(Order = 3)]
    public bool DetailedMode { get; set; } = false;

    [Category("03. Windows calls (restart required)")]
    [DisplayName("Profile Windows API calls")]
    [Description("Count Sleep/SleepEx/SleepConditionVariableCS/SwitchToThread/NtDelayExecution/NtWait*, " +
                 "tick/QPC and timer-resolution calls through native IAT stubs. Records the first argument and " +
                 "return-address rings; no managed work runs per call.")]
    [DefaultValue(true)]
    [Display(Order = 4)]
    public bool ProfileWindowsCalls { get; set; } = true;

    [Category("03. Windows calls (restart required)")]
    [DisplayName("Include all non-runtime modules")]
    [Description("Off = game, D3D11/DXGI, input and Steam modules. On = every non-runtime, non-OS module " +
                 "except the probe, Reloaded and .NET assemblies. Catches calls made from engine helper DLLs.")]
    [DefaultValue(true)]
    [Display(Order = 5)]
    public bool ProfileAllModules { get; set; } = true;
}

/// <summary>
/// Allows you to override certain aspects of the configuration creation process (e.g. create multiple configurations).
/// Override elements in <see cref="ConfiguratorMixinBase"/> for finer control.
/// </summary>
public class ConfiguratorMixin : ConfiguratorMixinBase
{
}
