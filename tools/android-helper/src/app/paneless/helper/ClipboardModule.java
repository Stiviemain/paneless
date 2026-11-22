package app.paneless.helper;

import android.content.ClipData;
import android.os.Binder;
import android.os.IBinder;
import android.os.Parcel;

import org.json.JSONObject;

import java.lang.reflect.Method;
import java.lang.reflect.Proxy;

/**
 * Always-on bidirectional text clipboard bridge via Android's IClipboard Binder service.
 * Uses com.android.shell as callingPackage (matching UID 2000) and supports API 26-37
 * signature variations reflectively. Zero idle CPU/battery: only fires on actual copy events.
 */
public final class ClipboardModule {
    private static final String SHELL_PKG = "com.android.shell";
    private static final String LISTENER_IFACE = "android.content.IOnPrimaryClipChangedListener";
    private static final int MAX_SYNC_CHARS = 256 * 1024;

    private final DaemonServer server;
    private final Object lock = new Object();
    private Object clipboardService;
    private Object listenerProxy;
    private boolean watching;
    private volatile String lastSetText = "";
    private volatile long lastSetMs;

    public ClipboardModule(DaemonServer server) {
        this.server = server;
        server.addCloseHook(this::stopWatching);
    }

    public void register(DaemonServer server) {
        server.handle("clipboard_get", (req, bin) -> {
            String text = getText();
            JSONObject res = new JSONObject();
            res.put("text", text != null ? text : "");
            return DaemonServer.Result.ok(res);
        });

        server.handle("clipboard_set", (req, bin) -> {
            String text = req.optString("text", "");
            if (text.length() > MAX_SYNC_CHARS) {
                return DaemonServer.Result.fail("Clipboard text exceeds 256 KB limit.");
            }
            setText(text);
            return DaemonServer.Result.ok();
        });

        server.handle("clipboard_watch", (req, bin) -> {
            boolean enabled = req.optBoolean("enabled", false);
            if (enabled) startWatching();
            else stopWatching();
            JSONObject res = new JSONObject();
            res.put("watching", watching);
            return DaemonServer.Result.ok(res);
        });
    }

    private Object service() throws Exception {
        if (clipboardService != null) return clipboardService;
        Class<?> sm = Class.forName("android.os.ServiceManager");
        IBinder binder = (IBinder) sm.getDeclaredMethod("getService", String.class).invoke(null, "clipboard");
        if (binder == null) throw new IllegalStateException("Clipboard service unavailable.");
        Class<?> stub = Class.forName("android.content.IClipboard$Stub");
        clipboardService = stub.getDeclaredMethod("asInterface", IBinder.class).invoke(null, binder);
        return clipboardService;
    }

    public String getText() throws Exception {
        Object svc = service();
        ClipData clip = invokeGetPrimaryClip(svc);
        if (clip == null || clip.getItemCount() == 0) return "";
        CharSequence cs = clip.getItemAt(0).getText();
        return cs != null ? cs.toString() : "";
    }

    public void setText(String text) throws Exception {
        Object svc = service();
        lastSetText = text;
        lastSetMs = System.currentTimeMillis();
        ClipData clip = ClipData.newPlainText("Paneless", text);
        invokeSetPrimaryClip(svc, clip);
    }

    private void startWatching() throws Exception {
        synchronized (lock) {
            if (watching) return;
            Object svc = service();
            Class<?> iface = Class.forName(LISTENER_IFACE);
            Binder binderImpl = new Binder() {
                @Override
                protected boolean onTransact(int code, Parcel data, Parcel reply, int flags) {
                    if (code == IBinder.FIRST_CALL_TRANSACTION) {
                        try { data.enforceInterface(LISTENER_IFACE); } catch (Throwable ignored) { }
                        onClipChanged();
                        return true;
                    }
                    try { return super.onTransact(code, data, reply, flags); }
                    catch (Throwable t) { return false; }
                }
            };
            Object proxy = Proxy.newProxyInstance(
                    iface.getClassLoader(),
                    new Class<?>[]{iface},
                    (p, method, args) -> {
                        String name = method.getName();
                        if ("asBinder".equals(name)) return binderImpl;
                        if ("dispatchPrimaryClipChanged".equals(name)) {
                            onClipChanged();
                            return null;
                        }
                        return null;
                    });
            invokeListenerOp(svc, "addPrimaryClipChangedListener", proxy);
            this.listenerProxy = proxy;
            this.watching = true;
        }
    }

    private void stopWatching() {
        synchronized (lock) {
            if (!watching || listenerProxy == null) return;
            try {
                invokeListenerOp(service(), "removePrimaryClipChangedListener", listenerProxy);
            } catch (Throwable ignored) { }
            listenerProxy = null;
            watching = false;
        }
    }

    private void onClipChanged() {
        try {
            String text = getText();
            if (text == null || text.isEmpty() || text.length() > MAX_SYNC_CHARS) return;
            // Suppress echo from our own clipboard_set within 1.5 s.
            if (text.equals(lastSetText) && System.currentTimeMillis() - lastSetMs < 1500) return;
            JSONObject ev = new JSONObject();
            ev.put("text", text);
            server.sendEvent("clipboard", ev);
        } catch (Throwable ignored) { }
    }

    // ---------------------------------------------------------------- reflective API 26-37 adapters

    private static ClipData invokeGetPrimaryClip(Object svc) throws Exception {
        for (Method m : svc.getClass().getMethods()) {
            if (!"getPrimaryClip".equals(m.getName())) continue;
            Class<?>[] p = m.getParameterTypes();
            if (p.length == 1 && p[0] == String.class)
                return (ClipData) m.invoke(svc, SHELL_PKG);
            if (p.length == 2 && p[0] == String.class && p[1] == int.class)
                return (ClipData) m.invoke(svc, SHELL_PKG, 0);
            if (p.length == 3 && p[0] == String.class && p[1] == String.class && p[2] == int.class)
                return (ClipData) m.invoke(svc, SHELL_PKG, null, 0);
            if (p.length == 4 && p[0] == String.class && p[1] == String.class && p[2] == int.class && p[3] == int.class)
                return (ClipData) m.invoke(svc, SHELL_PKG, null, 0, 0);
            if (p.length == 5 && p[0] == String.class && p[1] == String.class && p[2] == int.class && p[3] == int.class && p[4] == boolean.class)
                return (ClipData) m.invoke(svc, SHELL_PKG, null, 0, 0, false);
        }
        throw new NoSuchMethodException("No matching IClipboard.getPrimaryClip overload found.");
    }

    private static void invokeSetPrimaryClip(Object svc, ClipData clip) throws Exception {
        for (Method m : svc.getClass().getMethods()) {
            if (!"setPrimaryClip".equals(m.getName())) continue;
            Class<?>[] p = m.getParameterTypes();
            if (p.length == 2 && p[0] == ClipData.class && p[1] == String.class) {
                m.invoke(svc, clip, SHELL_PKG); return;
            }
            if (p.length == 3 && p[0] == ClipData.class && p[1] == String.class && p[2] == int.class) {
                m.invoke(svc, clip, SHELL_PKG, 0); return;
            }
            if (p.length == 4 && p[0] == ClipData.class && p[1] == String.class && p[2] == String.class && p[3] == int.class) {
                m.invoke(svc, clip, SHELL_PKG, null, 0); return;
            }
            if (p.length == 5 && p[0] == ClipData.class && p[1] == String.class && p[2] == String.class && p[3] == int.class && p[4] == int.class) {
                m.invoke(svc, clip, SHELL_PKG, null, 0, 0); return;
            }
            if (p.length == 6 && p[0] == ClipData.class && p[1] == String.class && p[2] == String.class && p[3] == int.class && p[4] == int.class && p[5] == boolean.class) {
                m.invoke(svc, clip, SHELL_PKG, null, 0, 0, false); return;
            }
        }
        throw new NoSuchMethodException("No matching IClipboard.setPrimaryClip overload found.");
    }

    private static void invokeListenerOp(Object svc, String methodName, Object listener) throws Exception {
        for (Method m : svc.getClass().getMethods()) {
            if (!methodName.equals(m.getName())) continue;
            Class<?>[] p = m.getParameterTypes();
            if (p.length == 2 && p[1] == String.class) {
                m.invoke(svc, listener, SHELL_PKG); return;
            }
            if (p.length == 3 && p[1] == String.class && p[2] == int.class) {
                m.invoke(svc, listener, SHELL_PKG, 0); return;
            }
            if (p.length == 4 && p[1] == String.class && p[2] == String.class && p[3] == int.class) {
                m.invoke(svc, listener, SHELL_PKG, null, 0); return;
            }
            if (p.length == 5 && p[1] == String.class && p[2] == String.class && p[3] == int.class && p[4] == int.class) {
                m.invoke(svc, listener, SHELL_PKG, null, 0, 0); return;
            }
        }
        throw new NoSuchMethodException("No matching IClipboard." + methodName + " overload found.");
    }
}
