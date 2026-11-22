package app.paneless.helper;

import android.app.ActivityManager;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.graphics.Point;
import android.hardware.display.DisplayManager;
import android.os.BatteryManager;
import android.os.Build;
import android.os.PowerManager;
import android.os.StatFs;
import android.os.SystemClock;
import android.view.Display;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.File;
import java.lang.reflect.Method;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.NetworkInterface;
import java.util.Collections;
import java.util.List;

/**
 * Fast structured device inventory, rich app metadata (version + APK size), and
 * zero-polling thermal push alerts via PowerManager.OnThermalStatusChangedListener.
 */
public final class TelemetryModule {
    private final DaemonServer server;
    private final Context context;
    private final Object watchLock = new Object();
    private PowerManager.OnThermalStatusChangedListener thermalListener;

    public TelemetryModule(DaemonServer server) {
        this.server = server;
        this.context = server.context();
        server.addCloseHook(this::stopWatch);
    }

    public void register(DaemonServer server) {
        server.handle("inventory", (req, bin) -> handleInventory());
        server.handle("apps_detail", (req, bin) -> handleAppsDetail(req));
        server.handle("telemetry_watch", (req, bin) -> {
            boolean enabled = req.optBoolean("enabled", false);
            if (enabled) startWatch();
            else stopWatch();
            JSONObject res = new JSONObject();
            res.put("watching", thermalListener != null);
            return DaemonServer.Result.ok(res);
        });
    }

    private DaemonServer.Result handleInventory() throws Exception {
        JSONObject res = new JSONObject();
        res.put("manufacturer", Build.MANUFACTURER != null ? Build.MANUFACTURER : "");
        res.put("model", Build.MODEL != null ? Build.MODEL : "");
        res.put("androidVersion", Build.VERSION.RELEASE != null ? Build.VERSION.RELEASE : "");
        res.put("sdk", Build.VERSION.SDK_INT);
        res.put("securityPatch", Build.VERSION.SECURITY_PATCH != null ? Build.VERSION.SECURITY_PATCH : "");
        res.put("serial", getProp("ro.serialno"));

        // Battery via sticky ACTION_BATTERY_CHANGED intent (fast, works in systemContext) + BatteryManager fallback.
        int level = -1;
        boolean charging = false;
        boolean full = false;
        int health = 0;
        double tempC = Double.NaN;
        try {
            Intent bat = context.registerReceiver(null, new IntentFilter(Intent.ACTION_BATTERY_CHANGED));
            if (bat != null) {
                int lv = bat.getIntExtra(BatteryManager.EXTRA_LEVEL, -1);
                int sc = bat.getIntExtra(BatteryManager.EXTRA_SCALE, 100);
                if (lv >= 0 && sc > 0) level = (int) Math.round(100.0 * lv / sc);
                int st = bat.getIntExtra(BatteryManager.EXTRA_STATUS, -1);
                charging = st == BatteryManager.BATTERY_STATUS_CHARGING || st == BatteryManager.BATTERY_STATUS_FULL;
                full = st == BatteryManager.BATTERY_STATUS_FULL;
                health = bat.getIntExtra(BatteryManager.EXTRA_HEALTH, 0);
                int t = bat.getIntExtra(BatteryManager.EXTRA_TEMPERATURE, Integer.MIN_VALUE);
                if (t != Integer.MIN_VALUE) tempC = t / 10.0;
            }
        } catch (Throwable ignored) { }

        if (level < 0) {
            try {
                BatteryManager bm = (BatteryManager) context.getSystemService(Context.BATTERY_SERVICE);
                if (bm != null) {
                    int cap = bm.getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY);
                    if (cap >= 0 && cap <= 100) level = cap;
                    charging = bm.isCharging();
                }
            } catch (Throwable ignored) { }
        }
        if (level >= 0) res.put("batteryLevel", level);
        res.put("charging", charging);
        res.put("full", full);
        if (health > 0) res.put("batteryHealthCode", health);
        if (!Double.isNaN(tempC)) res.put("batteryTempC", tempC);

        // Storage (/data)
        try {
            StatFs sf = new StatFs("/data");
            res.put("storageTotalMb", sf.getTotalBytes() / (1024L * 1024L));
            res.put("storageFreeMb", sf.getAvailableBytes() / (1024L * 1024L));
        } catch (Throwable ignored) { }

        // RAM
        try {
            ActivityManager am = (ActivityManager) context.getSystemService(Context.ACTIVITY_SERVICE);
            if (am != null) {
                ActivityManager.MemoryInfo mi = new ActivityManager.MemoryInfo();
                am.getMemoryInfo(mi);
                res.put("ramTotalMb", mi.totalMem / (1024L * 1024L));
                res.put("ramAvailMb", mi.availMem / (1024L * 1024L));
            }
        } catch (Throwable ignored) { }

        // Screen size
        try {
            DisplayManager dm = (DisplayManager) context.getSystemService(Context.DISPLAY_SERVICE);
            Display d = dm != null ? dm.getDisplay(0) : null;
            if (d != null) {
                Point p = new Point();
                d.getRealSize(p);
                res.put("screen", p.x + "x" + p.y);
            }
        } catch (Throwable ignored) { }

        // IPv4 address (prefer wlan0)
        res.put("ipAddress", findIpv4());
        res.put("uptimeSeconds", SystemClock.elapsedRealtime() / 1000L);

        // Thermal status (API 29+)
        if (Build.VERSION.SDK_INT >= 29) {
            try {
                PowerManager pm = (PowerManager) context.getSystemService(Context.POWER_SERVICE);
                if (pm != null) {
                    int ts = pm.getCurrentThermalStatus();
                    res.put("thermalStatus", ts);
                    res.put("thermalName", thermalName(ts));
                }
            } catch (Throwable ignored) { }
        }

        return DaemonServer.Result.ok(res);
    }

    @SuppressWarnings("deprecation")
    private DaemonServer.Result handleAppsDetail(JSONObject req) throws Exception {
        boolean includeSystem = req.optBoolean("includeSystem", false);
        PackageManager pm = context.getPackageManager();
        List<PackageInfo> pkgs = pm.getInstalledPackages(0);
        JSONArray arr = new JSONArray();
        for (PackageInfo pi : pkgs) {
            if (pi == null || pi.applicationInfo == null) continue;
            ApplicationInfo ai = pi.applicationInfo;
            boolean isSystem = (ai.flags & ApplicationInfo.FLAG_SYSTEM) != 0;
            if (!includeSystem && isSystem) continue;

            long apkBytes = 0;
            if (ai.sourceDir != null) apkBytes += new File(ai.sourceDir).length();
            if (ai.splitSourceDirs != null) {
                for (String s : ai.splitSourceDirs) {
                    if (s != null) apkBytes += new File(s).length();
                }
            }

            JSONObject o = new JSONObject();
            o.put("package", pi.packageName);
            o.put("label", AppsModule.clean(pm.getApplicationLabel(ai)));
            o.put("versionName", pi.versionName != null ? pi.versionName : "");
            o.put("versionCode", Build.VERSION.SDK_INT >= 28 ? pi.getLongVersionCode() : pi.versionCode);
            o.put("disabled", !ai.enabled);
            o.put("isSystem", isSystem);
            o.put("apkSizeBytes", apkBytes);
            o.put("lastUpdateMs", pi.lastUpdateTime);
            arr.put(o);
        }
        JSONObject res = new JSONObject();
        res.put("apps", arr);
        return DaemonServer.Result.ok(res);
    }

    private void startWatch() {
        if (Build.VERSION.SDK_INT < 29) return;
        synchronized (watchLock) {
            if (thermalListener != null) return;
            PowerManager pm = (PowerManager) context.getSystemService(Context.POWER_SERVICE);
            if (pm == null) return;
            thermalListener = status -> {
                // Only push meaningful thermal events (MODERATE+ or return to NONE) so idle Wi-Fi stays quiet.
                if (status == PowerManager.THERMAL_STATUS_LIGHT) return;
                try {
                    JSONObject ev = new JSONObject();
                    ev.put("status", status);
                    ev.put("name", thermalName(status));
                    server.sendEvent("thermal", ev);
                } catch (Throwable ignored) { }
            };
            try {
                pm.addThermalStatusListener(thermalListener);
            } catch (Throwable t) {
                thermalListener = null;
            }
        }
    }

    private void stopWatch() {
        if (Build.VERSION.SDK_INT < 29) return;
        synchronized (watchLock) {
            if (thermalListener != null) {
                try {
                    PowerManager pm = (PowerManager) context.getSystemService(Context.POWER_SERVICE);
                    if (pm != null) pm.removeThermalStatusListener(thermalListener);
                } catch (Throwable ignored) { }
                thermalListener = null;
            }
        }
    }

    private static String getProp(String key) {
        try {
            Class<?> sp = Class.forName("android.os.SystemProperties");
            Method m = sp.getMethod("get", String.class, String.class);
            String v = (String) m.invoke(null, key, "");
            return v != null ? v : "";
        } catch (Throwable ignored) { return ""; }
    }

    private static String findIpv4() {
        String fallback = "";
        try {
            for (NetworkInterface nif : Collections.list(NetworkInterface.getNetworkInterfaces())) {
                if (nif == null || nif.isLoopback() || !nif.isUp()) continue;
                for (InetAddress addr : Collections.list(nif.getInetAddresses())) {
                    if (!(addr instanceof Inet4Address)) continue;
                    String ip = addr.getHostAddress();
                    if (ip == null || ip.startsWith("127.") || ip.startsWith("169.254.")) continue;
                    if (nif.getName() != null && nif.getName().startsWith("wlan")) return ip;
                    if (fallback.isEmpty()) fallback = ip;
                }
            }
        } catch (Throwable ignored) { }
        return fallback;
    }

    private static String thermalName(int s) {
        switch (s) {
            case 0: return "NONE";
            case 1: return "LIGHT";
            case 2: return "MODERATE";
            case 3: return "SEVERE";
            case 4: return "CRITICAL";
            case 5: return "EMERGENCY";
            case 6: return "SHUTDOWN";
            default: return "UNKNOWN";
        }
    }
}
