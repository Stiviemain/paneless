package app.paneless.helper;

import android.content.ComponentName;
import android.content.Context;
import android.content.pm.PackageManager;
import android.os.IBinder;

import org.json.JSONArray;
import org.json.JSONObject;

import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.util.List;

/**
 * Lists running tasks across main and virtual displays, hands off live tasks between displays
 * (moveRootTaskToDisplay), and launches apps/deep-link URIs onto specific virtual displays.
 */
public final class TasksModule {
    private final Context context;
    private Object atm;
    private Object am;

    public TasksModule(DaemonServer server) {
        this.context = server.context();
    }

    public void register(DaemonServer server) {
        server.handle("tasks_list", (req, bin) -> handleList(req));
        server.handle("task_move", (req, bin) -> handleMove(req));
        server.handle("launch_intent", (req, bin) -> handleLaunch(req));
    }

    private Object activityTaskManager() {
        if (atm != null) return atm;
        try {
            Class<?> sm = Class.forName("android.os.ServiceManager");
            IBinder b = (IBinder) sm.getDeclaredMethod("getService", String.class).invoke(null, "activity_task");
            if (b == null) return null;
            Class<?> stub = Class.forName("android.app.IActivityTaskManager$Stub");
            atm = stub.getDeclaredMethod("asInterface", IBinder.class).invoke(null, b);
        } catch (Throwable ignored) { }
        return atm;
    }

    private Object activityManager() {
        if (am != null) return am;
        try {
            Class<?> sm = Class.forName("android.os.ServiceManager");
            IBinder b = (IBinder) sm.getDeclaredMethod("getService", String.class).invoke(null, "activity");
            if (b == null) return null;
            Class<?> stub = Class.forName("android.app.IActivityManager$Stub");
            am = stub.getDeclaredMethod("asInterface", IBinder.class).invoke(null, b);
        } catch (Throwable ignored) { }
        return am;
    }

    private DaemonServer.Result handleList(JSONObject req) throws Exception {
        int maxNum = Math.max(1, Math.min(50, req.optInt("maxNum", 25)));
        PackageManager pm = context.getPackageManager();
        JSONArray arr = new JSONArray();

        Object svc = activityTaskManager();
        List<?> infos = null;
        if (svc != null) {
            try {
                Method m = svc.getClass().getMethod("getAllRootTaskInfos");
                infos = (List<?>) m.invoke(svc);
            } catch (Throwable ignored) { }
            if (infos == null) {
                try {
                    Method m = svc.getClass().getMethod("getTasks", int.class);
                    infos = (List<?>) m.invoke(svc, maxNum);
                } catch (Throwable ignored) { }
            }
        }
        if (infos == null && activityManager() != null) {
            try {
                Method m = activityManager().getClass().getMethod("getTasks", int.class);
                infos = (List<?>) m.invoke(activityManager(), maxNum);
            } catch (Throwable ignored) { }
        }

        if (infos != null) {
            for (Object info : infos) {
                if (info == null || arr.length() >= maxNum) continue;
                int taskId = readInt(info, "taskId", readInt(info, "id", -1));
                if (taskId < 0) continue;
                int displayId = readInt(info, "displayId", 0);
                boolean visible = readBool(info, "visible", readBool(info, "isVisible", false));
                ComponentName top = readComponent(info, "topActivity");
                if (top == null) top = readComponent(info, "baseActivity");
                String pkg = top != null ? top.getPackageName() : "";
                String act = top != null ? top.getClassName() : "";
                if (pkg.isEmpty()) continue;

                String label = pkg;
                try {
                    label = AppsModule.clean(pm.getApplicationLabel(pm.getApplicationInfo(pkg, 0)));
                } catch (Throwable ignored) { }

                JSONObject o = new JSONObject();
                o.put("taskId", taskId);
                o.put("displayId", displayId);
                o.put("package", pkg);
                o.put("activity", act);
                o.put("label", label);
                o.put("visible", visible);
                arr.put(o);
            }
        }

        JSONObject res = new JSONObject();
        res.put("tasks", arr);
        return DaemonServer.Result.ok(res);
    }

    private DaemonServer.Result handleMove(JSONObject req) throws Exception {
        int taskId = req.optInt("taskId", -1);
        int displayId = req.optInt("displayId", 0);
        if (taskId < 0) return DaemonServer.Result.fail("Missing taskId.");

        Object svc = activityTaskManager();
        if (svc != null) {
            for (String name : new String[]{"moveRootTaskToDisplay", "moveStackToDisplay"}) {
                try {
                    Method m = svc.getClass().getMethod(name, int.class, int.class);
                    m.invoke(svc, taskId, displayId);
                    JSONObject res = new JSONObject();
                    res.put("taskId", taskId);
                    res.put("displayId", displayId);
                    return DaemonServer.Result.ok(res);
                } catch (NoSuchMethodException ignored) { }
            }
        }

        // Shell fallback works across OEMs when Binder method signature varies.
        Process p = new ProcessBuilder("am", "display", "move-stack", String.valueOf(taskId), String.valueOf(displayId))
                .redirectErrorStream(true).start();
        int code = p.waitFor();
        if (code != 0) return DaemonServer.Result.fail("Could not move task " + taskId + " to display " + displayId);
        JSONObject res = new JSONObject();
        res.put("taskId", taskId);
        res.put("displayId", displayId);
        return DaemonServer.Result.ok(res);
    }

    private DaemonServer.Result handleLaunch(JSONObject req) throws Exception {
        String pkg = req.optString("package", "").trim();
        String uri = req.optString("uri", "").trim();
        String action = req.optString("action", "").trim();
        int displayId = req.optInt("displayId", 0);

        if (pkg.isEmpty() && uri.isEmpty()) {
            return DaemonServer.Result.fail("Specify package or uri.");
        }

        java.util.ArrayList<String> cmd = new java.util.ArrayList<>();
        cmd.add("am");
        cmd.add("start");
        if (displayId > 0) {
            cmd.add("--display");
            cmd.add(String.valueOf(displayId));
        }
        if (!uri.isEmpty()) {
            cmd.add("-a");
            cmd.add(action.isEmpty() ? "android.intent.action.VIEW" : action);
            cmd.add("-d");
            cmd.add(uri);
            if (!pkg.isEmpty()) {
                cmd.add("-p");
                cmd.add(pkg);
            }
        } else {
            // Resolve launcher component for clean display-targeted start.
            android.content.Intent launch = context.getPackageManager().getLaunchIntentForPackage(pkg);
            if (launch != null && launch.getComponent() != null) {
                cmd.add("-n");
                cmd.add(launch.getComponent().flattenToShortString());
            } else {
                cmd.add("-a");
                cmd.add("android.intent.action.MAIN");
                cmd.add("-c");
                cmd.add("android.intent.category.LAUNCHER");
                cmd.add("-p");
                cmd.add(pkg);
            }
        }

        Process p = new ProcessBuilder(cmd).redirectErrorStream(true).start();
        byte[] out = p.getInputStream().readAllBytes();
        int code = p.waitFor();
        String text = new String(out, java.nio.charset.StandardCharsets.UTF_8).trim();
        if (code != 0 || text.contains("Error:")) {
            return DaemonServer.Result.fail(text.isEmpty() ? "Launch failed" : text);
        }
        JSONObject res = new JSONObject();
        res.put("output", text);
        res.put("displayId", displayId);
        return DaemonServer.Result.ok(res);
    }

    private static int readInt(Object o, String field, int fallback) {
        try {
            Field f = findField(o.getClass(), field);
            return f != null ? f.getInt(o) : fallback;
        } catch (Throwable ignored) { return fallback; }
    }

    private static boolean readBool(Object o, String field, boolean fallback) {
        try {
            Field f = findField(o.getClass(), field);
            return f != null ? f.getBoolean(o) : fallback;
        } catch (Throwable ignored) { return fallback; }
    }

    private static ComponentName readComponent(Object o, String field) {
        try {
            Field f = findField(o.getClass(), field);
            Object v = f != null ? f.get(o) : null;
            return v instanceof ComponentName ? (ComponentName) v : null;
        } catch (Throwable ignored) { return null; }
    }

    private static Field findField(Class<?> cls, String name) {
        for (Class<?> c = cls; c != null; c = c.getSuperclass()) {
            try {
                Field f = c.getDeclaredField(name);
                f.setAccessible(true);
                return f;
            } catch (NoSuchFieldException ignored) { }
        }
        return null;
    }
}
