package app.paneless.helper;

import android.content.Context;
import android.os.Looper;

import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.Method;

/**
 * Paneless' on-device helper, started like scrcpy's server:
 *   CLASSPATH=/data/local/tmp/paneless-helper.jar app_process / app.paneless.helper.Main apps
 *   CLASSPATH=... app_process / app.paneless.helper.Main icons <size> <package>...
 *   CLASSPATH=... app_process / app.paneless.helper.Main daemon <socketName>
 *   CLASSPATH=... app_process / app.paneless.helper.Main version
 * Nothing is installed on the device; the daemon exits cleanly when Paneless disconnects.
 */
public final class Main {
    public static void main(String[] args) throws Exception {
        if (args.length == 0) {
            System.err.println("usage: version | apps | icons <size> <package>... | daemon <socket>");
            System.exit(2);
        }
        if ("version".equals(args[0])) {
            System.out.println(DaemonServer.PROTOCOL_VERSION);
            System.exit(0);
        }

        exemptHiddenApis();
        Context ctx = systemContext();
        AppsModule apps = new AppsModule(ctx);

        if ("apps".equals(args[0])) {
            System.out.print(apps.listLauncherAppsTsv());
            System.out.flush();
            System.exit(0);
        } else if ("icons".equals(args[0]) && args.length >= 2) {
            int size = Math.max(16, Math.min(512, Integer.parseInt(args[1])));
            StringBuilder out = new StringBuilder();
            for (int i = 2; i < args.length; i++) {
                String pkg = args[i];
                AppsModule.IconInfo info = apps.loadIcon(pkg, size);
                if (info != null) {
                    out.append(pkg).append('\t').append(info.name).append('\t').append(info.pngBase64).append('\n');
                } else {
                    out.append(pkg).append('\t').append('\t').append('\n');
                }
            }
            System.out.print(out);
            System.out.flush();
            System.exit(0);
        } else if ("daemon".equals(args[0]) && args.length >= 2) {
            try {
                DaemonServer server = new DaemonServer(ctx);
                registerSafe("apps", () -> apps.register(server));
                UiModule[] uiHolder = new UiModule[1];
                registerSafe("ui", () -> { uiHolder[0] = new UiModule(server); uiHolder[0].register(server); });
                registerSafe("clipboard", () -> new ClipboardModule(server).register(server));
                registerSafe("files", () -> new FilesModule(server).register(server));
                registerSafe("tasks", () -> new TasksModule(server).register(server));
                registerSafe("screen", () -> new ScreenModule(server).register(server));
                registerSafe("telemetry", () -> new TelemetryModule(server).register(server));
                registerSafe("utils", () -> new DeviceUtilsModule(server).register(server));
                registerSafe("controls", () -> new ControlsModule(server, uiHolder[0]).register(server));
                server.run(args[1]);
                System.exit(0);
            } catch (Throwable t) {
                t.printStackTrace(System.err);
                System.err.flush();
                System.exit(1);
            }
        } else {
            System.err.println("unknown command: " + args[0]);
            System.exit(2);
        }
    }

    private static void registerSafe(String name, Runnable init) {
        try { init.run(); }
        catch (Throwable t) { System.err.println("module " + name + " disabled: " + t); }
    }

    /** Exempts this shell app_process from Android 9+ non-SDK hidden API restrictions where permitted. */
    static void exemptHiddenApis() {
        try {
            Method forName = Class.class.getDeclaredMethod("forName", String.class);
            Method getDeclaredMethod = Class.class.getDeclaredMethod("getDeclaredMethod", String.class, Class[].class);
            Class<?> vmRuntimeClass = (Class<?>) forName.invoke(null, "dalvik.system.VMRuntime");
            Method getRuntime = (Method) getDeclaredMethod.invoke(vmRuntimeClass, "getRuntime", null);
            Method setExemptions = (Method) getDeclaredMethod.invoke(vmRuntimeClass, "setHiddenApiExemptions", new Class<?>[]{String[].class});
            Object runtime = getRuntime.invoke(null);
            setExemptions.invoke(runtime, (Object) new String[]{"L"});
        } catch (Throwable ignored) {
            // Shell UID already has broad access on most builds; proceed defensively.
        }
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
