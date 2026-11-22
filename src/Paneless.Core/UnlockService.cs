using System.Text.RegularExpressions;

namespace Paneless.Core;

public sealed record UnlockResult(bool Ok, string Message);

/// <summary>Saves per-device unlock codes and enters them on a locked device, with safeguards:
/// it types only when the lock screen is confirmed to be in front, and pauses itself after a failed try.</summary>
public sealed partial class UnlockService(AdbClient adb, DataStore data, ISecretVault vault)
{
    public ISecretVault Vault => vault;

    public bool HasCode(string? sn) => sn != null && data.Unlock.ContainsKey(sn);
    public bool IsReady(string? sn) => sn != null && data.Unlock.TryGetValue(sn, out var u) && u.Enabled;

    public async Task SaveCodeAsync(string sn, string code)
    {
        var token = await vault.ProtectAsync(sn, code);
        data.Unlock[sn] = new UnlockEntry { Secret = token, Enabled = true };
        data.SaveUnlock();
    }

    public async Task RemoveCodeAsync(string sn)
    {
        if (data.Unlock.TryGetValue(sn, out var u)) await vault.ForgetAsync(sn, u.Secret ?? "");
        data.Unlock.Remove(sn);
        data.SaveUnlock();
    }

    void Pause(string sn)
    {
        if (data.Unlock.TryGetValue(sn, out var u)) { u.Enabled = false; data.SaveUnlock(); }
    }

    public sealed record LockInfo(bool? Locked, bool OnLockScreen);

    public async Task<LockInfo> GetLockInfoAsync(string serial)
    {
        // Use "dumpsys window displays" so we read mCurrentFocus specifically from Display 0;
        // Android 16/17 devices with virtual displays list those displays before Display 0.
        var r = await adb.ShellAsync(serial, "dumpsys trust | grep deviceLocked; dumpsys window displays | grep -E 'Display: mDisplayId=|mCurrentFocus=|isKeyguardShowing'");
        return ParseLockInfo(r.Output);
    }

    public static LockInfo ParseLockInfo(string output)
    {
        var lines = output.Split('\n');
        var trust = lines.Where(l => l.Contains("deviceLocked=")).ToList();
        var line = trust.FirstOrDefault(l => l.Contains("(current)")) ?? trust.FirstOrDefault();
        bool? locked = line != null && LockedRx().Match(line) is { Success: true } m ? m.Groups[1].Value == "1" : null;

        int? currentDisplay = null;
        string? display0Focus = null;
        string? fallbackFocus = null;
        var keyguardShowing = false;

        foreach (var raw in lines)
        {
            if (DisplayIdRx().Match(raw) is { Success: true } dm && int.TryParse(dm.Groups[1].Value, out var did))
            {
                currentDisplay = did;
                continue;
            }
            if (raw.Contains("isKeyguardShowing=true", StringComparison.Ordinal)) keyguardShowing = true;
            if (raw.Contains("mCurrentFocus=", StringComparison.Ordinal))
            {
                if (currentDisplay == 0) display0Focus = raw;
                else if (currentDisplay == null && fallbackFocus == null) fallbackFocus = raw;
            }
        }

        var focus = display0Focus ?? fallbackFocus ?? "";
        return new LockInfo(locked, LockScreenRx().IsMatch(focus) || (keyguardShowing && focus.Contains("mCurrentFocus=null", StringComparison.Ordinal)));
    }

    public async Task<UnlockResult> UnlockAsync(string serial, string sn, string name, bool wakeIfUnlocked = true)
    {
        if (!data.Unlock.TryGetValue(sn, out var entry)) return new(false, Text.F("No unlock code is saved for {0}.", name));
        if (!entry.Enabled) return new(false, Text.F("Auto-unlock for {0} is paused after a failed try. Save its code again to turn it back on.", name));

        var st = await GetLockInfoAsync(serial);
        if (st.Locked == null) return new(false, Text.F("Couldn't read the lock state of {0}, so nothing was typed.", name));
        if (st.Locked == false)
        {
            if (wakeIfUnlocked) await adb.ShellAsync(serial, "input keyevent KEYCODE_WAKEUP", TimeSpan.FromSeconds(5));
            return new(true, Text.F("{0} is already unlocked.", name));
        }

        // Wake the screen and ask for the PIN/password pad.
        await adb.ShellAsync(serial, "input keyevent KEYCODE_WAKEUP; sleep 0.4; wm dismiss-keyguard; sleep 0.9");
        st = await GetLockInfoAsync(serial);
        if (st.Locked == false) return new(true, Text.F("{0} unlocked.", name));
        // Only type when the lock screen is definitely in front, so the code can never land in an app.
        if (!st.OnLockScreen) return new(false, Text.F("The lock screen on {0} didn't come up as expected, so nothing was typed.", name));

        var code = await vault.RevealAsync(sn, entry.Secret ?? "");
        if (string.IsNullOrEmpty(code))
        {
            Pause(sn);
            return new(false, Text.F("The saved code for {0} couldn't be read. Save it again.", name));
        }
        var quoted = ("'" + code.Replace("'", "'\\''") + "'").Replace(" ", "%s");
        await adb.ShellAsync(serial, "input text " + quoted);
        code = null; quoted = null;
        await Task.Delay(700);

        st = await GetLockInfoAsync(serial);
        if (st.Locked == true)
        {
            // PIN auto-confirm is off (or it's a password): press Enter, still only on the lock screen.
            if (st.OnLockScreen) await adb.ShellAsync(serial, "input keyevent KEYCODE_ENTER", TimeSpan.FromSeconds(5));
            await Task.Delay(1200);
            st = await GetLockInfoAsync(serial);
        }
        if (st.Locked != false)
        {
            Pause(sn);
            return new(false, Text.F("Couldn't unlock {0}. Auto-unlock is paused for it so repeated tries can't lock you out. Check the code and save it again.", name));
        }
        return new(true, Text.F("{0} unlocked.", name));
    }

    [GeneratedRegex(@"Display:\s*mDisplayId=(\d+)")]
    private static partial Regex DisplayIdRx();
    [GeneratedRegex(@"deviceLocked=(\d)")]
    private static partial Regex LockedRx();
    [GeneratedRegex("NotificationShade|Keyguard|Bouncer|StatusBar")]
    private static partial Regex LockScreenRx();
}
