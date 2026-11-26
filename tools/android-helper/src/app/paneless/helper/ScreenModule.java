package app.paneless.helper;

import android.content.ContentResolver;
import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.hardware.display.DisplayManager;
import android.os.PowerManager;
import android.provider.Settings;
import android.view.Display;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;

/**
 * Fast, lightweight screen capture (on-device downscaled JPEG) and event-driven display/screen-state
 * reporting for Paneless' Wall and screen checks. Cuts Wall Wi-Fi payload by ~100x vs full-res PNG.
 * All system services are fetched lazily and defensively (KeyguardManager is avoided because on
 * Android 17 it triggers WindowManagerGlobal -> ApplicationSharedMemory which isn't initialized in app_process).
 */
public final class ScreenModule {
    private final DaemonServer server;
    private final Context context;
    private PowerManager power;
    private DisplayManager displayManager;
    private final Object watchLock = new Object();
    private final Object dimIoLock = new Object();
    private DisplayManager.DisplayListener displayListener;
    private Boolean lastDisplayOn;
    private boolean displayWatchRequested;
    private boolean dimActive;
    private boolean dimSawScreenOn;
    private String savedBrightness;
    private String savedBrightnessMode;

    public ScreenModule(DaemonServer server) {
        this.server = server;
        this.context = server.context();
        server.addCloseHook(() -> {
            restoreIfDimmed(false);
            synchronized (watchLock) {
                displayWatchRequested = false;
                maybeStopDisplayListenerLocked();
            }
        });
    }

    private PowerManager power() {
        if (power == null) {
            try { power = (PowerManager) context.getSystemService(Context.POWER_SERVICE); } catch (Throwable ignored) { }
        }
        return power;
    }

    private DisplayManager displayManager() {
        if (displayManager == null) {
            try { displayManager = (DisplayManager) context.getSystemService(Context.DISPLAY_SERVICE); } catch (Throwable ignored) { }
        }
        return displayManager;
    }

    public void register(DaemonServer server) {
        server.handle("screen_state", (req, bin) -> handleState());
        server.handle("capture", (req, bin) -> handleCapture(req));
        server.handle("display_watch", (req, bin) -> {
            boolean enabled = req.optBoolean("enabled", false);
            boolean watching;
            synchronized (watchLock) {
                displayWatchRequested = enabled;
                if (enabled) ensureDisplayListenerLocked();
                else maybeStopDisplayListenerLocked();
                watching = displayWatchRequested && displayListener != null;
            }
            JSONObject res = new JSONObject();
            res.put("watching", watching);
            return DaemonServer.Result.ok(res);
        });
        server.handle("screen_dim", (req, bin) -> handleScreenDim(req));
    }

    public boolean isDisplayOn(int displayId) {
        PowerManager pm = power();
        if (displayId == 0 && pm != null && !pm.isInteractive()) return false;
        DisplayManager dm = displayManager();
        if (dm != null) {
            try {
                Display d = dm.getDisplay(displayId);
                if (d != null) return d.getState() == Display.STATE_ON;
            } catch (Throwable ignored) { }
        }
        return pm == null || pm.isInteractive();
    }

    private DaemonServer.Result handleState() throws Exception {
        PowerManager pm = power();
        DisplayManager dm = displayManager();
        JSONObject res = new JSONObject();
        res.put("on", isDisplayOn(0));
        res.put("interactive", pm != null && pm.isInteractive());
        JSONArray displays = new JSONArray();
        if (dm != null) {
            try {
                for (Display d : dm.getDisplays()) {
                    if (d == null) continue;
                    JSONObject o = new JSONObject();
                    o.put("id", d.getDisplayId());
                    o.put("state", stateName(d.getState()));
                    o.put("on", d.getState() == Display.STATE_ON);
                    o.put("rotation", d.getRotation());
                    android.graphics.Point sz = new android.graphics.Point();
                    d.getRealSize(sz);
                    o.put("width", sz.x);
                    o.put("height", sz.y);
                    displays.put(o);
                }
            } catch (Throwable ignored) { }
        }
        res.put("displays", displays);
        return DaemonServer.Result.ok(res);
    }

    private DaemonServer.Result handleCapture(JSONObject req) throws Exception {
        int displayId = req.optInt("displayId", 0);
        int maxWidth = Math.max(120, Math.min(1920, req.optInt("maxWidth", 480)));
        int quality = Math.max(40, Math.min(95, req.optInt("quality", 78)));

        if (!isDisplayOn(displayId)) {
            JSONObject res = new JSONObject();
            res.put("screenOff", true);
            return DaemonServer.Result.ok(res);
        }

        Bitmap bmp = captureRawBitmap();
        if (bmp == null) {
            bmp = capturePngFallback(maxWidth);
        }
        if (bmp == null) {
            return DaemonServer.Result.fail("Screen capture returned no image.");
        }

        try {
            int w = bmp.getWidth();
            int h = bmp.getHeight();
            Bitmap scaled = bmp;
            if (w > maxWidth) {
                int targetH = Math.max(1, Math.round((float) h * maxWidth / w));
                scaled = Bitmap.createScaledBitmap(bmp, maxWidth, targetH, true);
            }
            ByteArrayOutputStream out = new ByteArrayOutputStream(32 * 1024);
            try {
                scaled.compress(Bitmap.CompressFormat.JPEG, quality, out);
            } finally {
                if (scaled != bmp) scaled.recycle();
            }

            JSONObject res = new JSONObject();
            res.put("screenOff", false);
            res.put("mime", "image/jpeg");
            res.put("width", Math.min(w, maxWidth));
            res.put("sourceWidth", w);
            res.put("sourceHeight", h);
            return DaemonServer.Result.ok(res, out.toByteArray());
        } finally {
            bmp.recycle();
        }
    }

    /** Captures raw RGBA framebuffer via local screencap (avoids slow full-res PNG encoding on-device). */
    private static Bitmap captureRawBitmap() {
        Process p = null;
        try {
            p = new ProcessBuilder("screencap").start();
            byte[] raw = readAll(p.getInputStream());
            int code = p.waitFor();
            if (code != 0 || raw.length < 16) return null;

            ByteBuffer hdr = ByteBuffer.wrap(raw, 0, 16).order(ByteOrder.LITTLE_ENDIAN);
            int w = hdr.getInt(0);
            int h = hdr.getInt(4);
            int fmt = hdr.getInt(8);
            if (w <= 0 || h <= 0 || w > 8192 || h > 8192 || fmt != 1 /* PIXEL_FORMAT_RGBA_8888 */) return null;
            int pixelBytes = w * h * 4;
            int headerLen = raw.length - pixelBytes;
            if (headerLen != 12 && headerLen != 16) return null;

            Bitmap bmp = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888);
            bmp.copyPixelsFromBuffer(ByteBuffer.wrap(raw, headerLen, pixelBytes));
            return bmp;
        } catch (Throwable ignored) {
            return null;
        } finally {
            if (p != null) p.destroy();
        }
    }

    private static Bitmap capturePngFallback(int maxWidth) {
        Process p = null;
        try {
            p = new ProcessBuilder("screencap", "-p").start();
            byte[] png = readAll(p.getInputStream());
            int code = p.waitFor();
            if (code != 0 || png.length < 16) return null;

            BitmapFactory.Options opts = new BitmapFactory.Options();
            opts.inJustDecodeBounds = true;
            BitmapFactory.decodeByteArray(png, 0, png.length, opts);
            int sample = 1;
            while (opts.outWidth > 0 && opts.outWidth / (sample * 2) >= maxWidth) sample *= 2;
            opts.inJustDecodeBounds = false;
            opts.inSampleSize = sample;
            return BitmapFactory.decodeByteArray(png, 0, png.length, opts);
        } catch (Throwable ignored) {
            return null;
        } finally {
            if (p != null) p.destroy();
        }
    }

    private static byte[] readAll(InputStream in) throws Exception {
        ByteArrayOutputStream out = new ByteArrayOutputStream(65536);
        byte[] buf = new byte[65536];
        int n;
        while ((n = in.read(buf)) >= 0) {
            if (n > 0) out.write(buf, 0, n);
        }
        return out.toByteArray();
    }

    private DaemonServer.Result handleScreenDim(JSONObject req) throws Exception {
        boolean enabled = req.optBoolean("enabled", false);
        if (!enabled) {
            boolean restored = restoreIfDimmed(false);
            JSONObject res = new JSONObject();
            res.put("dimmed", false);
            res.put("restored", restored);
            return DaemonServer.Result.ok(res);
        }

        int target = Math.max(0, Math.min(255, req.optInt("brightness", 1)));
        synchronized (dimIoLock) {
            boolean needSnapshot;
            synchronized (watchLock) {
                needSnapshot = !dimActive;
            }
            String curBrightness = needSnapshot ? readSystemSetting("screen_brightness") : null;
            String curMode = needSnapshot ? readSystemSetting("screen_brightness_mode") : null;
            synchronized (watchLock) {
                if (!dimActive) {
                    savedBrightness = curBrightness;
                    savedBrightnessMode = curMode;
                    dimActive = true;
                    dimSawScreenOn = isDisplayOn(0);
                    ensureDisplayListenerLocked();
                }
            }
            runCmd("settings", "put", "system", "screen_brightness_mode", "0");
            runCmd("settings", "put", "system", "screen_brightness", String.valueOf(target));
        }
        JSONObject res = new JSONObject();
        res.put("dimmed", true);
        return DaemonServer.Result.ok(res);
    }

    private boolean restoreIfDimmed(boolean onlyIfScreenOff) {
        synchronized (dimIoLock) {
            String b;
            String m;
            synchronized (watchLock) {
                if (!dimActive) return false;
                if (onlyIfScreenOff && (!dimSawScreenOn || isDisplayOn(0))) return false;
                b = savedBrightness;
                m = savedBrightnessMode;
                dimActive = false;
                dimSawScreenOn = false;
                savedBrightness = null;
                savedBrightnessMode = null;
                maybeStopDisplayListenerLocked();
            }
            try {
                if (b != null && !b.isEmpty() && !"null".equals(b)) {
                    runCmd("settings", "put", "system", "screen_brightness", b);
                }
                if (m != null && !m.isEmpty() && !"null".equals(m)) {
                    runCmd("settings", "put", "system", "screen_brightness_mode", m);
                }
            } catch (Throwable ignored) { }
            return true;
        }
    }

    public void abandonDim() {
        synchronized (dimIoLock) {
            synchronized (watchLock) {
                dimActive = false;
                dimSawScreenOn = false;
                savedBrightness = null;
                savedBrightnessMode = null;
                maybeStopDisplayListenerLocked();
            }
        }
    }

    private String readSystemSetting(String key) {
        try {
            ContentResolver cr = context.getContentResolver();
            if (cr != null) {
                String v = Settings.System.getString(cr, key);
                if (v != null && !v.isEmpty()) return v.trim();
            }
        } catch (Throwable ignored) { }
        try {
            String v = runCmdOut("settings", "get", "system", key).trim();
            if (!v.isEmpty() && !"null".equals(v)) return v;
        } catch (Throwable ignored) { }
        return null;
    }

    private static void runCmd(String... args) throws Exception {
        Process p = new ProcessBuilder(args).redirectErrorStream(true).start();
        try {
            p.getInputStream().readAllBytes();
            p.waitFor();
        } finally {
            p.destroy();
        }
    }

    private static String runCmdOut(String... args) throws Exception {
        Process p = new ProcessBuilder(args).redirectErrorStream(true).start();
        try {
            byte[] out = p.getInputStream().readAllBytes();
            p.waitFor();
            return new String(out, StandardCharsets.UTF_8);
        } finally {
            p.destroy();
        }
    }

    private void ensureDisplayListenerLocked() {
        DisplayManager dm = displayManager();
        if (displayListener != null || dm == null) return;
        if (!displayWatchRequested && !dimActive) return;
        lastDisplayOn = isDisplayOn(0);
        displayListener = new DisplayManager.DisplayListener() {
            @Override public void onDisplayAdded(int displayId) { }
            @Override public void onDisplayRemoved(int displayId) { }
            @Override
            public void onDisplayChanged(int displayId) {
                if (displayId != 0) return;
                boolean on = isDisplayOn(0);
                boolean shouldRestore = false;
                boolean sendEvent = false;
                synchronized (watchLock) {
                    if (dimActive) {
                        if (on) {
                            dimSawScreenOn = true;
                        } else if (dimSawScreenOn) {
                            shouldRestore = true;
                        }
                    }
                    if (displayWatchRequested && (lastDisplayOn == null || lastDisplayOn != on)) {
                        lastDisplayOn = on;
                        sendEvent = true;
                    }
                }
                if (shouldRestore) {
                    new Thread(() -> restoreIfDimmed(true), "paneless-dim-restore").start();
                }
                if (sendEvent) {
                    try {
                        JSONObject ev = new JSONObject();
                        ev.put("on", on);
                        server.sendEvent("screen_state", ev);
                    } catch (Throwable ignored) { }
                }
            }
        };
        try { dm.registerDisplayListener(displayListener, server.handler()); }
        catch (Throwable ignored) { displayListener = null; }
    }

    private void maybeStopDisplayListenerLocked() {
        if (displayWatchRequested || dimActive) return;
        DisplayManager dm = displayManager();
        if (displayListener != null && dm != null) {
            try { dm.unregisterDisplayListener(displayListener); } catch (Throwable ignored) { }
        }
        displayListener = null;
        lastDisplayOn = null;
    }

    private static String stateName(int s) {
        switch (s) {
            case Display.STATE_ON: return "ON";
            case Display.STATE_OFF: return "OFF";
            case Display.STATE_DOZE: return "DOZE";
            case Display.STATE_DOZE_SUSPEND: return "DOZE_SUSPEND";
            default: return "UNKNOWN";
        }
    }
}
