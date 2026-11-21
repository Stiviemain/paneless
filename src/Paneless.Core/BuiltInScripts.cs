namespace Paneless.Core;

/// <summary>Common adb commands with plain-language descriptions. Syntax checked on Android 17 (Pixel 11 Pro)
/// against each command's own help text. Fields in {braces} are asked for when the script runs.</summary>
public static class BuiltInScripts
{
    const string Battery = "Battery and power";
    const string Display = "Display and animations";
    const string Apps = "Apps";
    const string Network = "Network";
    const string Info = "Info and diagnostics";
    const string Capture = "Screenshots and recording";
    const string Power = "Restart and advanced";

    static AdbScript S(string category, string name, string description, string commands, bool confirm = false, string? warning = null) =>
        new()
        {
            Id = "builtin:" + name, BuiltIn = true, Category = Text.T(category), Name = Text.T(name), Description = Text.T(description),
            Commands = commands, Confirm = confirm, Warning = warning == null ? null : Text.T(warning),
        };

    /// <summary>Built on first use, so names and descriptions come out in the app's language.</summary>
    public static IReadOnlyList<AdbScript> All => _all ??= Build();
    static IReadOnlyList<AdbScript>? _all;

    static IReadOnlyList<AdbScript> Build() =>
    [
        // ---------------------------------------------------------------- battery and power
        S(Battery, "Battery status",
            "Shows charge level, whether it's charging, health and temperature (in tenths of a degree: 297 = 29.7 °C).",
            "adb shell dumpsys battery"),
        S(Battery, "Turn off Doze (deep sleep)",
            "Stops Android's Doze battery saver, so apps keep running normally while the screen is off. Uses more battery. Android turns Doze back on after a restart.",
            "adb shell dumpsys deviceidle disable"),
        S(Battery, "Turn Doze back on",
            "Re-enables Doze, Android's normal battery saving while the screen is off.",
            "adb shell dumpsys deviceidle enable"),
        S(Battery, "Exempt an app from battery optimisation",
            "Lets one app run in the background without Doze limits (useful for alarms, messaging or home-automation apps). Enter its package name, e.g. com.whatsapp.",
            "adb shell dumpsys deviceidle whitelist +{package}"),
        S(Battery, "Remove an app's battery exemption",
            "Puts an app back under normal battery optimisation.",
            "adb shell dumpsys deviceidle whitelist -{package}"),
        S(Battery, "List battery-optimisation exemptions",
            "Shows which apps are allowed to ignore Doze.",
            "adb shell dumpsys deviceidle whitelist"),
        S(Battery, "Stay awake while charging",
            "Keeps the screen on whenever the device is plugged in (same as the Developer options switch).",
            "adb shell svc power stayon true"),
        S(Battery, "Normal sleep while charging",
            "Turns 'Stay awake while charging' off again.",
            "adb shell svc power stayon false"),
        S(Battery, "Set screen timeout",
            "Sets how long the screen stays on without touch, in milliseconds (600000 = 10 minutes, 60000 = 1 minute).",
            "adb shell settings put system screen_off_timeout {milliseconds=600000}"),

        // ---------------------------------------------------------------- display
        S(Display, "Faster animations (0.5×)",
            "Halves window, transition and animation durations, so the device feels snappier. Same as the Developer options animation scales.",
            "adb shell settings put global window_animation_scale 0.5\nadb shell settings put global transition_animation_scale 0.5\nadb shell settings put global animator_duration_scale 0.5"),
        S(Display, "Animations off",
            "Turns system animations off completely. Some apps look abrupt without them.",
            "adb shell settings put global window_animation_scale 0\nadb shell settings put global transition_animation_scale 0\nadb shell settings put global animator_duration_scale 0"),
        S(Display, "Normal animations (1×)",
            "Restores the default animation speed.",
            "adb shell settings put global window_animation_scale 1\nadb shell settings put global transition_animation_scale 1\nadb shell settings put global animator_duration_scale 1"),
        S(Display, "Show touches",
            "Shows a dot wherever the screen is touched. Handy while mirroring, for demos or recordings.",
            "adb shell settings put system show_touches 1"),
        S(Display, "Hide touches",
            "Turns the touch dots off again.",
            "adb shell settings put system show_touches 0"),
        S(Display, "Screen size and density",
            "Shows the resolution and the dpi (density) the device uses. 'Override' means the display size was changed.",
            "adb shell wm size\nadb shell wm density"),
        S(Display, "Change display density (dpi)",
            "Makes everything on screen smaller (higher dpi) or bigger (lower dpi). Use 'Reset display density' to undo.",
            "adb shell wm density {dpi=420}",
            warning: "Changes the layout of every app until you reset it."),
        S(Display, "Reset display density",
            "Goes back to the device's normal display size.",
            "adb shell wm density reset"),
        S(Display, "Keep the highest refresh rate",
            "Stops the screen dropping to a lower refresh rate (e.g. forces 120 Hz). Smoother, but uses more battery. Works on Pixel and most phones with a fast display.",
            "adb shell settings put system min_refresh_rate {hz=120}"),
        S(Display, "Normal refresh rate",
            "Lets the screen lower its refresh rate again to save battery.",
            "adb shell settings delete system min_refresh_rate"),

        // ---------------------------------------------------------------- apps
        S(Apps, "List your installed apps",
            "Package names of every app you installed (not the system ones). You need these names for the other app scripts.",
            "adb shell pm list packages -3"),
        S(Apps, "List disabled apps",
            "Package names of apps that are currently disabled.",
            "adb shell pm list packages -d"),
        S(Apps, "App in front right now",
            "Shows which app and screen currently has focus, including its package name.",
            "adb shell dumpsys window | grep -E 'mCurrentFocus|mFocusedApp'"),
        S(Apps, "Open an app's info page",
            "Opens the app's settings page (storage, permissions, battery) on the device.",
            "adb shell am start -a android.settings.APPLICATION_DETAILS_SETTINGS -d package:{package}"),
        S(Apps, "Force-stop an app",
            "Closes an app completely, like 'Force stop' in its settings. It can be opened again normally.",
            "adb shell am force-stop {package}"),
        S(Apps, "Clear an app's data",
            "Deletes all of an app's data: logins, settings, downloads, caches. The app starts like it was just installed.",
            "adb shell pm clear {package}",
            confirm: true, warning: "Can't be undone."),
        S(Apps, "Disable an app (e.g. bloatware)",
            "Switches an app off without uninstalling it. It disappears from the launcher and stops running. Undo with 'Re-enable an app'.",
            "adb shell pm disable-user --user 0 {package}",
            confirm: true, warning: "Disabling an important system app can break features. Only disable apps you recognise."),
        S(Apps, "Re-enable an app",
            "Turns a disabled app back on.",
            "adb shell pm enable {package}"),
        S(Apps, "Remove an app for this user",
            "Uninstalls an app (also pre-installed ones) for your user; a system app's original copy stays on the device. Undo with 'Restore a removed system app'.",
            "adb shell pm uninstall -k --user 0 {package}",
            confirm: true, warning: "Removing an important system app can break features."),
        S(Apps, "Restore a removed system app",
            "Brings back a pre-installed app that was removed with 'Remove an app for this user'.",
            "adb shell cmd package install-existing {package}"),
        S(Apps, "Grant a special permission",
            "Grants a permission an app can't ask for itself. Some automation and launcher apps ask you to run this (usually WRITE_SECURE_SETTINGS).",
            "adb shell pm grant {package} {permission=android.permission.WRITE_SECURE_SETTINGS}",
            confirm: true, warning: "Only grant permissions an app's own instructions ask for."),

        // ---------------------------------------------------------------- network
        S(Network, "Wi-Fi IP address",
            "Shows the device's IP address on your Wi-Fi.",
            "adb shell ip -4 addr show wlan0"),
        S(Network, "Private DNS on",
            "Sends all DNS lookups to a private DNS provider (encrypted). The default blocks ads and trackers; replace it with any provider's hostname, e.g. one.one.one.one.",
            "adb shell settings put global private_dns_specifier {hostname=dns.adguard-dns.com}\nadb shell settings put global private_dns_mode hostname"),
        S(Network, "Private DNS automatic",
            "Goes back to Android's default: encrypted DNS when your network supports it.",
            "adb shell settings put global private_dns_mode opportunistic"),
        S(Network, "Airplane mode on",
            "Turns airplane mode on.",
            "adb shell cmd connectivity airplane-mode enable",
            confirm: true, warning: "If Wi-Fi switches off with it, Paneless loses the connection; turn airplane mode off on the device."),
        S(Network, "Airplane mode off",
            "Turns airplane mode off.",
            "adb shell cmd connectivity airplane-mode disable"),

        // ---------------------------------------------------------------- info
        S(Info, "Device info",
            "Brand, model, Android version, API level and security patch date.",
            "adb shell getprop ro.product.brand\nadb shell getprop ro.product.model\nadb shell getprop ro.build.version.release\nadb shell getprop ro.build.version.sdk\nadb shell getprop ro.build.version.security_patch"),
        S(Info, "Storage space",
            "How much space is used and free on the device's main storage.",
            "adb shell df -h /data"),
        S(Info, "Memory (RAM)",
            "Total, free and available memory.",
            "adb shell grep -E 'MemTotal|MemFree|MemAvailable' /proc/meminfo"),
        S(Info, "Busiest processes",
            "The 10 processes using the most CPU right now.",
            "adb shell top -b -n 1 -m 10"),
        S(Info, "Recent errors (log)",
            "The last 300 error lines from the system log. Useful when an app keeps crashing.",
            "adb logcat -d -t 300 *:E"),

        // ---------------------------------------------------------------- capture
        S(Capture, "Screenshot to PC",
            "Takes a screenshot and saves it as a PNG in Documents\\Paneless on this computer.",
            "adb shell screencap -p /sdcard/paneless-shot.png\nadb pull /sdcard/paneless-shot.png {device}-{time}.png\nadb shell rm /sdcard/paneless-shot.png"),
        S(Capture, "Record 10 seconds to PC",
            "Records the screen for 10 seconds and saves it as an MP4 in Documents\\Paneless on this computer.",
            "adb shell screenrecord --time-limit 10 /sdcard/paneless-rec.mp4\nadb pull /sdcard/paneless-rec.mp4 {device}-{time}.mp4\nadb shell rm /sdcard/paneless-rec.mp4"),

        // ---------------------------------------------------------------- restart and advanced
        S(Power, "Wake the screen",
            "Turns the screen on (doesn't unlock).",
            "adb shell input keyevent KEYCODE_WAKEUP"),
        S(Power, "Turn the screen off",
            "Turns the screen off, like a short press of the power button.",
            "adb shell input keyevent KEYCODE_SLEEP"),
        S(Power, "Restart",
            "Restarts the device.",
            "adb reboot",
            confirm: true, warning: "Disconnects. After it restarts, Wireless debugging may need to be switched on again."),
        S(Power, "Allow more background processes",
            "Turns off Android's 'phantom process killer', which stops apps such as Termux once all apps together start more than 32 background processes. On Android 14 and newer, Developer options also has 'Disable child process restrictions'. Can be reset by system updates.",
            "adb shell /system/bin/device_config set_sync_disabled_for_tests persistent\nadb shell /system/bin/device_config put activity_manager max_phantom_processes 2147483647\nadb shell settings put global settings_enable_monitor_phantom_procs false",
            confirm: true, warning: "Background apps can then use more battery and memory."),
        S(Power, "Restore background process limits",
            "Undoes 'Allow more background processes'.",
            "adb shell /system/bin/device_config set_sync_disabled_for_tests none\nadb shell /system/bin/device_config delete activity_manager max_phantom_processes\nadb shell settings delete global settings_enable_monitor_phantom_procs"),
    ];
}
