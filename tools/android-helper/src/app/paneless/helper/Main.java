package app.paneless.helper;

import android.content.Context;
import android.content.Intent;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import android.content.pm.ResolveInfo;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.drawable.Drawable;
import android.os.Looper;
import android.util.Base64;

import java.io.ByteArrayOutputStream;
import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.util.List;

/**
 * Paneless' helper on the device, started like scrcpy's server:
 *   CLASSPATH=/data/local/tmp/paneless-helper.jar app_process / app.paneless.helper.Main apps
 *   CLASSPATH=... app_process / app.paneless.helper.Main icons <size> <package>...
 * "apps" prints every app with a launcher icon: package TAB name.
 * "icons" prints package TAB name TAB base64 PNG of its launcher icon (as the home screen draws it, size x size).
 * It only reads; nothing is installed or changed.
 */
public final class Main {
    public static void main(String[] args) throws Exception {
        if (args.length == 0) { System.err.println("usage: apps | icons <size> <package>..."); System.exit(2); }
        PackageManager pm = systemContext().getPackageManager();
        StringBuilder out = new StringBuilder();
        if (args[0].equals("apps")) {
            Intent launcher = new Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER);
            List<ResolveInfo> found = pm.queryIntentActivities(launcher, 0);
            java.util.Set<String> seen = new java.util.HashSet<>();
            for (ResolveInfo ri : found) {
                String pkg = ri.activityInfo.packageName;
                if (!seen.add(pkg)) continue;
                out.append(pkg).append('\t').append(clean(ri.activityInfo.applicationInfo.loadLabel(pm))).append('\n');
            }
        } else if (args[0].equals("icons") && args.length >= 2) {
            int size = Math.max(16, Math.min(512, Integer.parseInt(args[1])));
            for (int i = 2; i < args.length; i++) {
                String pkg = args[i];
                try {
                    ApplicationInfo ai = pm.getApplicationInfo(pkg, 0);
                    Drawable icon = null;
                    Intent launch = pm.getLaunchIntentForPackage(pkg);
                    if (launch != null) { try { icon = pm.getActivityIcon(launch); } catch (Exception ignored) { } }
                    if (icon == null) icon = pm.getApplicationIcon(ai);
                    out.append(pkg).append('\t').append(clean(pm.getApplicationLabel(ai))).append('\t').append(png(icon, size)).append('\n');
                } catch (Throwable e) {
                    out.append(pkg).append('\t').append('\t').append('\n');   // not installed (or no icon): name and icon empty
                    System.err.println(pkg + ": " + e);
                    for (Throwable c = e.getCause(); c != null; c = c.getCause()) System.err.println("  caused by " + c);
                }
            }
        } else { System.err.println("unknown command: " + args[0]); System.exit(2); }
        System.out.print(out);
        System.out.flush();
        System.exit(0);   // the system context may have started threads
    }

    static String clean(CharSequence s) {
        return s == null ? "" : s.toString().replace('\t', ' ').replace('\n', ' ').replace('\r', ' ').trim();
    }

    static String png(Drawable d, int size) {
        Bitmap bmp = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888);
        Canvas c = new Canvas(bmp);
        d.setBounds(0, 0, size, size);
        d.draw(c);   // an adaptive icon draws with the device's own icon shape
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        bmp.compress(Bitmap.CompressFormat.PNG, 100, bytes);
        return Base64.encodeToString(bytes.toByteArray(), Base64.NO_WRAP);
    }

    /** The system context, as scrcpy gets it: app_process has no application of its own. */
    static Context systemContext() throws Exception {
        if (Looper.getMainLooper() == null) Looper.prepareMainLooper();
        Class<?> at = Class.forName("android.app.ActivityThread");
        Constructor<?> ctor = at.getDeclaredConstructor();
        ctor.setAccessible(true);
        Object thread = ctor.newInstance();
        Field current = at.getDeclaredField("sCurrentActivityThread");
        current.setAccessible(true);
        current.set(null, thread);
        Method get = at.getDeclaredMethod("getSystemContext");
        get.setAccessible(true);
        Context context = (Context) get.invoke(thread);
        // Some systems (Xiaomi's) ask the current Application for resources while loading an icon: give them one.
        try {
            android.app.Application app = new android.app.Application();
            Method attach = android.content.ContextWrapper.class.getDeclaredMethod("attachBaseContext", Context.class);
            attach.setAccessible(true);
            attach.invoke(app, context);
            Field initial = at.getDeclaredField("mInitialApplication");
            initial.setAccessible(true);
            initial.set(thread, app);
        } catch (Throwable e) {
            System.err.println("no application stand-in: " + e);
        }
        return context;
    }
}
