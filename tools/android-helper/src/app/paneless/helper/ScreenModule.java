package app.paneless.helper;

import android.app.KeyguardManager;
import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.hardware.display.DisplayManager;
import android.os.PowerManager;
import android.view.Display;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;

/**
 * Fast, lightweight screen capture (on-device downscaled JPEG) and event-driven display/screen-state
 * reporting for Paneless' Wall and screen checks. Cuts Wall Wi-Fi payload by ~100x vs full-res PNG.
 */
public final class ScreenModule {
    private final DaemonServer server;
    private final PowerManager power;
    private final KeyguardManager keyguard;
    private final DisplayManager displayManager;
    private final Object watchLock = new Object();
    private DisplayManager.DisplayListener displayListener;
    private Boolean lastDisplayOn;

    public ScreenModule(DaemonServer server) {
        this.server = server;
        Context ctx = server.context();
        this.power = (PowerManager) ctx.getSystemService(Context.POWER_SERVICE);
        this.keyguard = (KeyguardManager) ctx.getSystemService(Context.KEYGUARD_SERVICE);
        this.displayManager = (DisplayManager) ctx.getSystemService(Context.DISPLAY_SERVICE);
        server.addCloseHook(this::stopDisplayWatch);
    }

    public void register(DaemonServer server) {
        server.handle("screen_state", (req, bin) -> handleState());
        server.handle("capture", (req, bin) -> handleCapture(req));
        server.handle("display_watch", (req, bin) -> {
            boolean enabled = req.optBoolean("enabled", false);
            if (enabled) startDisplayWatch();
            else stopDisplayWatch();
            JSONObject res = new JSONObject();
            res.put("watching", displayListener != null);
            return DaemonServer.Result.ok(res);
        });
    }

    public boolean isDisplayOn(int displayId) {
        if (displayId == 0 && power != null && !power.isInteractive()) return false;
        if (displayManager != null) {
            Display d = displayManager.getDisplay(displayId);
            if (d != null) return d.getState() == Display.STATE_ON;
        }
        return power == null || power.isInteractive();
    }

    private DaemonServer.Result handleState() throws Exception {
        JSONObject res = new JSONObject();
        res.put("on", isDisplayOn(0));
        res.put("interactive", power != null && power.isInteractive());
        res.put("locked", keyguard != null && keyguard.isDeviceLocked());
        JSONArray displays = new JSONArray();
        if (displayManager != null) {
            for (Display d : displayManager.getDisplays()) {
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

    private void startDisplayWatch() {
        synchronized (watchLock) {
            if (displayListener != null || displayManager == null) return;
            lastDisplayOn = isDisplayOn(0);
            displayListener = new DisplayManager.DisplayListener() {
                @Override public void onDisplayAdded(int displayId) { }
                @Override public void onDisplayRemoved(int displayId) { }
                @Override
                public void onDisplayChanged(int displayId) {
                    if (displayId != 0) return;
                    boolean on = isDisplayOn(0);
                    if (lastDisplayOn == null || lastDisplayOn != on) {
                        lastDisplayOn = on;
                        try {
                            JSONObject ev = new JSONObject();
                            ev.put("on", on);
                            server.sendEvent("screen_state", ev);
                        } catch (Throwable ignored) { }
                    }
                }
            };
            displayManager.registerDisplayListener(displayListener, server.handler());
        }
    }

    private void stopDisplayWatch() {
        synchronized (watchLock) {
            if (displayListener != null && displayManager != null) {
                try { displayManager.unregisterDisplayListener(displayListener); } catch (Throwable ignored) { }
            }
            displayListener = null;
            lastDisplayOn = null;
        }
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
