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
        var r = await adb.ShellAsync(serial, "dumpsys trust | grep deviceLocked; dumpsys window | grep -E 'mCurrentFocus|isKeyguardShowing'");
        var lines = r.Output.Split('\n');
        var trust = lines.Where(l => l.Contains("deviceLocked=")).ToList();
        var line = trust.FirstOrDefault(l => l.Contains("(current)")) ?? trust.FirstOrDefault();
        bool? locked = line != null && LockedRx().Match(line) is { Success: true } m ? m.Groups[1].Value == "1" : null;
        var focus = lines.FirstOrDefault(l => l.Contains("mCurrentFocus=")) ?? "";
        return new LockInfo(locked, LockScreenRx().IsMatch(focus));
    }

    public async Task<UnlockResult> UnlockAsync(string serial, string sn, string name)
    {
        if (!data.Unlock.TryGetValue(sn, out var entry)) return new(false, $"No unlock code is saved for {name}.");
        if (!entry.Enabled) return new(false, $"Auto-unlock for {name} is paused after a failed try. Save its code again to turn it back on.");

        var st = await GetLockInfoAsync(serial);
        if (st.Locked == null) return new(false, $"Couldn't read the lock state of {name}, so nothing was typed.");
        if (st.Locked == false)
        {
            await adb.ShellAsync(serial, "input keyevent KEYCODE_WAKEUP", TimeSpan.FromSeconds(5));
            return new(true, $"{name} is already unlocked.");
        }

        // Wake the screen and ask for the PIN/password pad.
        await adb.ShellAsync(serial, "input keyevent KEYCODE_WAKEUP; sleep 0.4; wm dismiss-keyguard; sleep 0.9");
        st = await GetLockInfoAsync(serial);
        if (st.Locked == false) return new(true, $"{name} unlocked.");
        // Only type when the lock screen is definitely in front, so the code can never land in an app.
        if (!st.OnLockScreen) return new(false, $"The lock screen on {name} didn't come up as expected, so nothing was typed.");

        var code = await vault.RevealAsync(sn, entry.Secret ?? "");
        if (string.IsNullOrEmpty(code))
        {
            Pause(sn);
            return new(false, $"The saved code for {name} couldn't be read. Save it again.");
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
            return new(false, $"Couldn't unlock {name}. Auto-unlock is paused for it so repeated tries can't lock you out. Check the code and save it again.");
        }
        return new(true, $"{name} unlocked.");
    }

    [GeneratedRegex(@"deviceLocked=(\d)")]
    private static partial Regex LockedRx();
    [GeneratedRegex("NotificationShade|Keyguard|Bouncer|StatusBar")]
    private static partial Regex LockScreenRx();
}
