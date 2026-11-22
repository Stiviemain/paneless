package app.paneless.helper;

import android.content.Context;
import android.content.Intent;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import android.content.pm.ResolveInfo;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.drawable.Drawable;
import android.util.Base64;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.util.HashSet;
import java.util.List;
import java.util.Set;

/**
 * Launcher app listing and adaptive icon rendering via PackageManager.
 * Shared by both the legacy CLI commands and the persistent daemon RPCs.
 */
public final class AppsModule {
    private final PackageManager pm;

    public AppsModule(Context context) {
        this.pm = context.getPackageManager();
    }

    public void register(DaemonServer server) {
        server.handle("apps", (req, bin) -> {
            JSONArray list = listLauncherAppsJson();
            JSONObject res = new JSONObject();
            res.put("apps", list);
            return DaemonServer.Result.ok(res);
        });

        server.handle("icons", (req, bin) -> {
            int size = Math.max(16, Math.min(512, req.optInt("size", 128)));
            JSONArray pkgs = req.optJSONArray("packages");
            JSONArray items = new JSONArray();
            if (pkgs != null) {
                for (int i = 0; i < pkgs.length(); i++) {
                    String pkg = pkgs.optString(i, "");
                    if (pkg.isEmpty()) continue;
                    IconInfo info = loadIcon(pkg, size);
                    if (info == null) continue;
                    JSONObject obj = new JSONObject();
                    obj.put("package", pkg);
                    obj.put("name", info.name);
                    obj.put("png", info.pngBase64);
                    items.put(obj);
                }
            }
            JSONObject res = new JSONObject();
            res.put("icons", items);
            return DaemonServer.Result.ok(res);
        });
    }

    public JSONArray listLauncherAppsJson() throws Exception {
        Intent launcher = new Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER);
        List<ResolveInfo> found = pm.queryIntentActivities(launcher, 0);
        Set<String> seen = new HashSet<>();
        JSONArray arr = new JSONArray();
        for (ResolveInfo ri : found) {
            if (ri.activityInfo == null) continue;
            String pkg = ri.activityInfo.packageName;
            if (!seen.add(pkg)) continue;
            JSONObject obj = new JSONObject();
            obj.put("package", pkg);
            obj.put("name", clean(ri.activityInfo.applicationInfo.loadLabel(pm)));
            arr.put(obj);
        }
        return arr;
    }

    public String listLauncherAppsTsv() {
        Intent launcher = new Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER);
        List<ResolveInfo> found = pm.queryIntentActivities(launcher, 0);
        Set<String> seen = new HashSet<>();
        StringBuilder out = new StringBuilder();
        for (ResolveInfo ri : found) {
            if (ri.activityInfo == null) continue;
            String pkg = ri.activityInfo.packageName;
            if (!seen.add(pkg)) continue;
            out.append(pkg).append('\t').append(clean(ri.activityInfo.applicationInfo.loadLabel(pm))).append('\n');
        }
        return out.toString();
    }

    public static final class IconInfo {
        public final String name;
        public final String pngBase64;
        public IconInfo(String name, String pngBase64) { this.name = name; this.pngBase64 = pngBase64; }
    }

    public IconInfo loadIcon(String pkg, int size) {
        try {
            ApplicationInfo ai = pm.getApplicationInfo(pkg, 0);
            Drawable icon = null;
            Intent launch = pm.getLaunchIntentForPackage(pkg);
            if (launch != null) {
                try { icon = pm.getActivityIcon(launch); } catch (Exception ignored) { }
            }
            if (icon == null) icon = pm.getApplicationIcon(ai);
            return new IconInfo(clean(pm.getApplicationLabel(ai)), pngBase64(icon, size));
        } catch (Throwable e) {
            System.err.println(pkg + ": " + e);
            for (Throwable c = e.getCause(); c != null; c = c.getCause()) {
                System.err.println("  caused by " + c);
            }
            return null;
        }
    }

    public static String clean(CharSequence s) {
        return s == null ? "" : s.toString().replace('\t', ' ').replace('\n', ' ').replace('\r', ' ').trim();
    }

    public static String pngBase64(Drawable d, int size) {
        Bitmap bmp = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888);
        Canvas c = new Canvas(bmp);
        d.setBounds(0, 0, size, size);
        d.draw(c); // adaptive icons draw with the device's own mask shape
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        bmp.compress(Bitmap.CompressFormat.PNG, 100, bytes);
        bmp.recycle();
        return Base64.encodeToString(bytes.toByteArray(), Base64.NO_WRAP);
    }
}
