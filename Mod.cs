using System.Diagnostics;
using p5rpc.autonetwork.Configuration;
using p5rpc.autonetwork.Template;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.Enums;
using Reloaded.Hooks.Definitions.X64;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Memory.Sources;
using Reloaded.Mod.Interfaces;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace p5rpc.autonetwork;

public unsafe class Mod : ModBase
{
    private readonly ILogger _logger;
    private readonly IModConfig _modConfig;
    private readonly IReloadedHooks? _hooks;
    private Config _configuration;

    private readonly object _networkPatchLock = new();
    private nuint _networkCase1Addr;
    private nuint _networkDefaultAddr;

    private readonly object _introSkipHookLock = new();
    private nuint _introSkipTitleAddr;
    private nuint _netFlagAddr;
    private SetBitFlagDelegate? _setBitFlag;
    private StartNetLoginDelegate? _startNetLogin;
    private IAsmHook? _introSkipHook;
    private IReverseWrapper<OnIntroSkipDelegate>? _onIntroSkipWrapper;
    private bool _autoLoginTriggered;

    [Function(CallingConventions.Microsoft)]
    private delegate void OnIntroSkipDelegate();

    [Function(CallingConventions.Microsoft)]
    private delegate nuint SetBitFlagDelegate(uint flag, byte value);

    [Function(CallingConventions.Microsoft)]
    private delegate void StartNetLoginDelegate(nuint param1, byte value);

    public Mod(ModContext context)
    {
        _logger = context.Logger;
        _modConfig = context.ModConfig;
        _hooks = context.Hooks;
        _configuration = context.Configuration;

        if (!context.ModLoader.GetController<IStartupScanner>().TryGetTarget(out var startupScanner) || startupScanner == null)
        {
            _logger.WriteLine($"[{_modConfig.ModId}] Failed to get IStartupScanner. Make sure Reloaded.Memory.SigScan.ReloadedII is enabled.");
            return;
        }

        var baseAddr = (nuint)Process.GetCurrentProcess().MainModule!.BaseAddress;

        if (_configuration.AutoEnableNetwork)
        {
            ApplyAutoEnableNetworkPatch(startupScanner, baseAddr);
            SetupIntroSkipCompatHook(startupScanner, baseAddr);
        }

        if (_configuration.SkipNetworkResultPrompt)
            ApplySkipNetworkResultPatch(startupScanner, baseAddr);
    }

    /// <summary>
    /// Patches FUN_141379e30 (GAME_SEQUENCE_NEWTORK) at case 1 entry so that after case 0 initialization,
    /// state jumps directly to case 4 (enables network flag, sets bitflag 0x40000001, and spawns netLoginProc)
    /// without creating the "NetworkStateCofirm" UI dialog.
    /// </summary>
    private void ApplyAutoEnableNetworkPatch(IStartupScanner scanner, nuint baseAddr)
    {
        scanner.AddMainModuleScan(
            "48 8D 4F 48 E8 ?? ?? ?? ?? 66 44 89 6F 04 8B 47 10 48 89 77 14 83 E0 F1",
            res =>
            {
                if (!res.Found)
                {
                    _logger.WriteLine($"[{_modConfig.ModId}] AutoEnableNetwork: case 1 entry pattern not found.");
                    return;
                }

                lock (_networkPatchLock)
                {
                    _networkCase1Addr = baseAddr + (nuint)res.Offset + 9;
                    TryWriteAutoEnableNetworkPatch();
                }
            });

        scanner.AddMainModuleScan(
            "66 89 47 04 48 8D 4F 10 E8 ?? ?? ?? ?? 48 8D 4F 2C",
            res =>
            {
                if (!res.Found)
                {
                    _logger.WriteLine($"[{_modConfig.ModId}] AutoEnableNetwork: switch default pattern not found.");
                    return;
                }

                lock (_networkPatchLock)
                {
                    _networkDefaultAddr = baseAddr + (nuint)res.Offset + 4;
                    TryWriteAutoEnableNetworkPatch();
                }
            });
    }

    private void TryWriteAutoEnableNetworkPatch()
    {
        if (_networkCase1Addr == 0 || _networkDefaultAddr == 0)
            return;

        long rel = (long)_networkDefaultAddr - ((long)_networkCase1Addr + 11);
        if (rel < int.MinValue || rel > int.MaxValue)
        {
            _logger.WriteLine($"[{_modConfig.ModId}] AutoEnableNetwork: relative jump out of range (0x{rel:X}).");
            return;
        }

        int rel32 = (int)rel;
        byte[] patch =
        [
            0x66, 0xC7, 0x47, 0x04, 0x04, 0x00,
            0xE9,
            (byte)(rel32 & 0xFF),
            (byte)((rel32 >> 8) & 0xFF),
            (byte)((rel32 >> 16) & 0xFF),
            (byte)((rel32 >> 24) & 0xFF)
        ];

        Memory.Instance.SafeWriteRaw(_networkCase1Addr, patch);
        _logger.WriteLine($"[{_modConfig.ModId}] AutoEnableNetwork patch applied at 0x{_networkCase1Addr:X} -> 0x{_networkDefaultAddr:X}.");
    }

    /// <summary>
    /// Compatibility with Persona Essentials' Intro Skip:
    /// When Intro Skip jumps directly to Title (case 0x0C) in FUN_14137b500 case 7, GAME_SEQUENCE_NEWTORK is skipped.
    /// We parse DAT_1428df33e, thunk_FUN_14be54240, and FUN_1415171e0 from FUN_141379e30 case 4,
    /// and hook the MOV dword ptr [RSI], 0xC instruction to trigger network login if it hasn't run yet.
    /// </summary>
    private void SetupIntroSkipCompatHook(IStartupScanner scanner, nuint baseAddr)
    {
        if (_hooks == null)
        {
            _logger.WriteLine($"[{_modConfig.ModId}] Reloaded.Hooks not available; Persona Essentials Intro Skip compatibility disabled.");
            return;
        }

        // FUN_141379e30 case 4:
        // +0:  40 0F B6 D5             MOVZX EDX, BPL
        // +4:  40 88 2D [disp32]       MOV byte ptr [DAT_1428df33e], BPL  (next RIP = +11)
        // +11: B9 01 00 00 40          MOV ECX, 0x40000001
        // +16: E8 [rel32]              CALL thunk_FUN_14be54240           (next RIP = +21)
        // +21: 40 0F B6 D5             MOVZX EDX, BPL
        // +25: E8 [rel32]              CALL FUN_1415171e0                 (next RIP = +30)
        // +30: B8 05 00 00 00          MOV EAX, 5
        // +35: 66 89 47 04             MOV word ptr [RDI+4], AX
        scanner.AddMainModuleScan(
            "40 0F B6 D5 40 88 2D ?? ?? ?? ?? B9 01 00 00 40 E8 ?? ?? ?? ?? 40 0F B6 D5 E8 ?? ?? ?? ?? B8 05 00 00 00 66 89 47 04",
            res =>
            {
                if (!res.Found)
                {
                    _logger.WriteLine($"[{_modConfig.ModId}] IntroSkipCompat: case 4 pattern not found.");
                    return;
                }

                var case4Addr = baseAddr + (nuint)res.Offset;
                var netFlagAddr = (nuint)((long)(case4Addr + 11) + *(int*)(case4Addr + 7));
                var setBitFlagAddr = (nuint)((long)(case4Addr + 21) + *(int*)(case4Addr + 17));
                var startNetLoginAddr = (nuint)((long)(case4Addr + 30) + *(int*)(case4Addr + 26));

                lock (_introSkipHookLock)
                {
                    _netFlagAddr = netFlagAddr;
                    _setBitFlag = _hooks.CreateWrapper<SetBitFlagDelegate>((long)setBitFlagAddr, out _);
                    _startNetLogin = _hooks.CreateWrapper<StartNetLoginDelegate>((long)startNetLoginAddr, out _);
                    TryActivateIntroSkipCompatHook();
                }
            });

        // FUN_14137b500 case 7 (right after Persona Essentials' 74 10 -> 90 90 patch):
        // C7 06 0C 00 00 00   MOV dword ptr [RSI], 0xC
        // E8 ?? ?? ?? ??      CALL FUN_1403163c0
        // E9                  JMP ...
        scanner.AddMainModuleScan(
            "C7 06 0C 00 00 00 E8 ?? ?? ?? ?? E9",
            res =>
            {
                if (!res.Found)
                {
                    _logger.WriteLine($"[{_modConfig.ModId}] IntroSkipCompat: title jump pattern not found.");
                    return;
                }

                lock (_introSkipHookLock)
                {
                    _introSkipTitleAddr = baseAddr + (nuint)res.Offset;
                    TryActivateIntroSkipCompatHook();
                }
            });
    }

    private void TryActivateIntroSkipCompatHook()
    {
        if (_hooks == null || _introSkipTitleAddr == 0 || _netFlagAddr == 0 || _setBitFlag == null || _startNetLogin == null)
            return;

        string[] asm =
        [
            "use64",
            "push rax",
            "push rcx",
            "push rdx",
            "push r8",
            "push r9",
            "push r10",
            "push r11",
            "sub rsp, 0x28",
            _hooks.Utilities.GetAbsoluteCallMnemonics(OnIntroSkipToTitle, out _onIntroSkipWrapper),
            "add rsp, 0x28",
            "pop r11",
            "pop r10",
            "pop r9",
            "pop r8",
            "pop rdx",
            "pop rcx",
            "pop rax"
        ];

        _introSkipHook = _hooks.CreateAsmHook(asm, (long)_introSkipTitleAddr, AsmHookBehaviour.ExecuteFirst).Activate();
        _logger.WriteLine($"[{_modConfig.ModId}] IntroSkipCompat hook activated at 0x{_introSkipTitleAddr:X} (flag=0x{_netFlagAddr:X}).");
    }

    private void OnIntroSkipToTitle()
    {
        if (_autoLoginTriggered)
            return;

        _autoLoginTriggered = true;

        // If GAME_SEQUENCE_NEWTORK already ran and enabled network, do nothing.
        if (*(byte*)_netFlagAddr != 0)
            return;

        *(byte*)_netFlagAddr = 1;
        var ret = _setBitFlag!(0x40000001, 1);
        _startNetLogin!(ret, 1);
        _logger.WriteLine($"[{_modConfig.ModId}] Intro Skip detected; triggered automatic network login.");
    }

    /// <summary>
    /// Patches FUN_141379e30 case 6 so both login success (0x07) and login failure (0x0A)
    /// transition directly to state 0x0D (sync flag and exit GAME_SEQUENCE_NEWTORK) with no prompt.
    /// </summary>
    private void ApplySkipNetworkResultPatch(IStartupScanner scanner, nuint baseAddr)
    {
        scanner.AddMainModuleScan(
            "84 C0 74 0B BB 07 00 00 00 40 0F B6 D5 EB 07 BB 0A 00 00 00 32 D2",
            res =>
            {
                if (!res.Found)
                {
                    _logger.WriteLine($"[{_modConfig.ModId}] SkipNetworkResultPrompt: case 6 pattern not found.");
                    return;
                }

                var addr = baseAddr + (nuint)res.Offset;
                Memory.Instance.SafeWriteRaw(addr + 5, new byte[] { 0x0D });
                Memory.Instance.SafeWriteRaw(addr + 16, new byte[] { 0x0D });
                _logger.WriteLine($"[{_modConfig.ModId}] SkipNetworkResultPrompt patch applied at 0x{addr:X}.");
            });
    }

    public override void ConfigurationUpdated(Config configuration)
    {
        _configuration = configuration;
        _logger.WriteLine($"[{_modConfig.ModId}] Config updated (restart game for patch changes to take effect).");
    }

#pragma warning disable CS8618
    public Mod() { }
#pragma warning restore CS8618
}
