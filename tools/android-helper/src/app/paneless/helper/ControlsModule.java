package app.paneless.helper;

import android.app.Notification;
import android.app.UiModeManager;
import android.content.Context;
import android.media.AudioManager;
import android.os.Bundle;
import android.os.Parcelable;
import android.os.SystemClock;
import android.provider.Settings;
import android.view.KeyEvent;
import android.view.accessibility.AccessibilityEvent;

import org.json.JSONObject;

import java.nio.charset.StandardCharsets;
import java.util.List;
import java.util.Locale;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Typed system controls (dark mode, DND, brightness, auto-rotate, volumes, radios),
 * media playback state & controls, and strictly opt-in notification / 2FA OTP watcher.
 */
public final class ControlsModule {
    private static final Pattern OTP_KEYWORD = Pattern.compile(
            "(?i)\\b(code|otp|pin|passcode|token|verify|verification|confirm|confirmation|2fa|two[- ]factor|login|sign[- ]?in|security|sicherheitscode|bestätigungscode|einmalcode|prüfcode|vérification|authentification)\\b");
    private static final Pattern OTP_DIGITS = Pattern.compile("(?<!\\d)(\\d{4,8})(?!\\d)");
    private static final Pattern MEDIA_DESC = Pattern.compile("description=([^,\\n]+),\\s*([^,\\n]*),\\s*([^\\n]*)");
    private static final Pattern MEDIA_STATE = Pattern.compile("state=PlaybackState\\s*\\{state=(\\d+)");

    private final DaemonServer server;
    private final Context context;
    private final UiModule uiModule;
    private final ScreenModule screenModule;
    private volatile boolean watchingNotifications;

    public ControlsModule(DaemonServer server, UiModule uiModule, ScreenModule screenModule) {
        this.server = server;
        this.context = server.context();
        this.uiModule = uiModule;
        this.screenModule = screenModule;
        server.addCloseHook(() -> {
            if (watchingNotifications) {
                watchingNotifications = false;
                try { uiModule.setNotificationCallback(null); } catch (Throwable ignored) { }
            }
        });
    }

    public void register(DaemonServer server) {
        server.handle("sys_get", (req, bin) -> handleSysGet());
        server.handle("sys_set", (req, bin) -> handleSysSet(req));
        server.handle("media_state", (req, bin) -> handleMediaState());
        server.handle("media_control", (req, bin) -> handleMediaControl(req));
        server.handle("notifications_watch", (req, bin) -> handleNotificationsWatch(req));
    }

    private DaemonServer.Result handleSysGet() throws Exception {
        JSONObject res = new JSONObject();
        try {
            UiModeManager ui = (UiModeManager) context.getSystemService(Context.UI_MODE_SERVICE);
            if (ui != null) res.put("darkMode", ui.getNightMode() == UiModeManager.MODE_NIGHT_YES);
        } catch (Throwable ignored) { }

        try {
            res.put("brightness", Settings.System.getInt(context.getContentResolver(), Settings.System.SCREEN_BRIGHTNESS, 128));
            res.put("autoBrightness", Settings.System.getInt(context.getContentResolver(), Settings.System.SCREEN_BRIGHTNESS_MODE, 0) == 1);
            res.put("autoRotate", Settings.System.getInt(context.getContentResolver(), Settings.System.ACCELEROMETER_ROTATION, 0) == 1);
            res.put("screenTimeoutMs", Settings.System.getInt(context.getContentResolver(), Settings.System.SCREEN_OFF_TIMEOUT, 60000));
            res.put("dnd", Settings.Global.getInt(context.getContentResolver(), "zen_mode", 0) != 0);
            res.put("wifi", Settings.Global.getInt(context.getContentResolver(), Settings.Global.WIFI_ON, 0) != 0);
            res.put("bluetooth", Settings.Global.getInt(context.getContentResolver(), Settings.Global.BLUETOOTH_ON, 0) != 0);
        } catch (Throwable ignored) { }

        try {
            AudioManager am = (AudioManager) context.getSystemService(Context.AUDIO_SERVICE);
            if (am != null) {
                JSONObject vols = new JSONObject();
                vols.put("music", streamVol(am, AudioManager.STREAM_MUSIC));
                vols.put("ring", streamVol(am, AudioManager.STREAM_RING));
                vols.put("alarm", streamVol(am, AudioManager.STREAM_ALARM));
                vols.put("notification", streamVol(am, AudioManager.STREAM_NOTIFICATION));
                res.put("volumes", vols);
            }
        } catch (Throwable ignored) { }

        return DaemonServer.Result.ok(res);
    }

    private static JSONObject streamVol(AudioManager am, int stream) throws Exception {
        JSONObject o = new JSONObject();
        o.put("current", am.getStreamVolume(stream));
        o.put("max", am.getStreamMaxVolume(stream));
        return o;
    }

    private DaemonServer.Result handleSysSet(JSONObject req) throws Exception {
        String setting = req.optString("setting", "").trim();
        switch (setting) {
            case "darkMode": {
                boolean on = req.optBoolean("value", false);
                runCmd("cmd", "uimode", "night", on ? "yes" : "no");
                return DaemonServer.Result.ok();
            }
            case "dnd": {
                boolean on = req.optBoolean("value", false);
                runCmd("cmd", "notification", "set_dnd", on ? "on" : "off");
                return DaemonServer.Result.ok();
            }
            case "autoRotate": {
                boolean on = req.optBoolean("value", false);
                runCmd("settings", "put", "system", "accelerometer_rotation", on ? "1" : "0");
                return DaemonServer.Result.ok();
            }
            case "autoBrightness": {
                if (screenModule != null) screenModule.abandonDim();
                boolean on = req.optBoolean("value", false);
                runCmd("settings", "put", "system", "screen_brightness_mode", on ? "1" : "0");
                return DaemonServer.Result.ok();
            }
            case "brightness": {
                if (screenModule != null) screenModule.abandonDim();
                int val = Math.max(0, Math.min(255, req.optInt("value", 128)));
                runCmd("settings", "put", "system", "screen_brightness_mode", "0");
                runCmd("settings", "put", "system", "screen_brightness", String.valueOf(val));
                return DaemonServer.Result.ok();
            }
            case "wifi": {
                boolean on = req.optBoolean("value", true);
                runCmd("svc", "wifi", on ? "enable" : "disable");
                return DaemonServer.Result.ok();
            }
            case "bluetooth": {
                boolean on = req.optBoolean("value", true);
                runCmd("svc", "bluetooth", on ? "enable" : "disable");
                return DaemonServer.Result.ok();
            }
            case "volume": {
                AudioManager am = (AudioManager) context.getSystemService(Context.AUDIO_SERVICE);
                if (am == null) return DaemonServer.Result.fail("AudioManager unavailable.");
                String streamName = req.optString("stream", "music").toLowerCase(Locale.ROOT);
                int stream;
                switch (streamName) {
                    case "ring": stream = AudioManager.STREAM_RING; break;
                    case "alarm": stream = AudioManager.STREAM_ALARM; break;
                    case "notification": stream = AudioManager.STREAM_NOTIFICATION; break;
                    default: stream = AudioManager.STREAM_MUSIC; break;
                }
                int max = am.getStreamMaxVolume(stream);
                int level = Math.max(0, Math.min(max, req.optInt("value", max / 2)));
                am.setStreamVolume(stream, level, 0);
                return DaemonServer.Result.ok();
            }
            default:
                return DaemonServer.Result.fail("Unknown setting: " + setting);
        }
    }

    private DaemonServer.Result handleMediaState() throws Exception {
        String out = runCmd("dumpsys", "media_session");
        JSONObject res = new JSONObject();
        boolean playing = false;
        String title = "", artist = "", album = "", pkg = "";
        String curPkg = "";
        for (String raw : out.split("\n")) {
            String line = raw.trim();
            if (line.contains("package=")) {
                int idx = line.indexOf("package=");
                String rest = line.substring(idx + 8);
                int end = rest.indexOf(' ');
                curPkg = end > 0 ? rest.substring(0, end) : rest;
            }
            Matcher sm = MEDIA_STATE.matcher(line);
            if (sm.find()) {
                int st = Integer.parseInt(sm.group(1));
                if (st == 3 /* STATE_PLAYING */) {
                    playing = true;
                    pkg = curPkg;
                }
            }
            Matcher dm = MEDIA_DESC.matcher(line);
            if (dm.find()) {
                String t = dm.group(1).trim();
                String a = dm.group(2).trim();
                String al = dm.group(3).trim();
                if (!t.equals("null") && !t.isEmpty() && (title.isEmpty() || playing)) {
                    title = t;
                    artist = a.equals("null") ? "" : a;
                    album = al.equals("null") ? "" : al;
                    if (pkg.isEmpty()) pkg = curPkg;
                }
            }
        }
        res.put("playing", playing);
        res.put("title", title);
        res.put("artist", artist);
        res.put("album", album);
        res.put("package", pkg);
        return DaemonServer.Result.ok(res);
    }

    private DaemonServer.Result handleMediaControl(JSONObject req) throws Exception {
        String action = req.optString("action", "play_pause").toLowerCase(Locale.ROOT);
        int key;
        switch (action) {
            case "play_pause": key = KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE; break;
            case "play": key = KeyEvent.KEYCODE_MEDIA_PLAY; break;
            case "pause": key = KeyEvent.KEYCODE_MEDIA_PAUSE; break;
            case "next": key = KeyEvent.KEYCODE_MEDIA_NEXT; break;
            case "previous": case "prev": key = KeyEvent.KEYCODE_MEDIA_PREVIOUS; break;
            case "stop": key = KeyEvent.KEYCODE_MEDIA_STOP; break;
            default: return DaemonServer.Result.fail("Unknown media action: " + action);
        }
        AudioManager am = (AudioManager) context.getSystemService(Context.AUDIO_SERVICE);
        if (am != null) {
            long now = SystemClock.uptimeMillis();
            am.dispatchMediaKeyEvent(new KeyEvent(now, now, KeyEvent.ACTION_DOWN, key, 0));
            am.dispatchMediaKeyEvent(new KeyEvent(now, now, KeyEvent.ACTION_UP, key, 0));
        } else {
            runCmd("input", "keyevent", String.valueOf(key));
        }
        return DaemonServer.Result.ok();
    }

    private DaemonServer.Result handleNotificationsWatch(JSONObject req) throws Exception {
        if (uiModule == null) return DaemonServer.Result.fail("UiAutomation module unavailable.");
        boolean enabled = req.optBoolean("enabled", false);
        watchingNotifications = enabled;
        if (enabled) {
            uiModule.setNotificationCallback(this::onNotificationEvent);
        } else {
            uiModule.setNotificationCallback(null);
        }
        JSONObject res = new JSONObject();
        res.put("watching", watchingNotifications);
        return DaemonServer.Result.ok(res);
    }

    private void onNotificationEvent(AccessibilityEvent event) {
        if (!watchingNotifications || event == null) return;
        String pkg = event.getPackageName() != null ? event.getPackageName().toString() : "";
        String title = "";
        String text = "";

        Parcelable p = event.getParcelableData();
        if (p instanceof Notification) {
            Bundle extras = ((Notification) p).extras;
            if (extras != null) {
                CharSequence t = extras.getCharSequence(Notification.EXTRA_TITLE);
                CharSequence b = extras.getCharSequence(Notification.EXTRA_BIG_TEXT);
                if (b == null) b = extras.getCharSequence(Notification.EXTRA_TEXT);
                if (t != null) title = t.toString().trim();
                if (b != null) text = b.toString().trim();
            }
        }
        if (text.isEmpty()) {
            List<CharSequence> parts = event.getText();
            if (parts != null && !parts.isEmpty()) {
                StringBuilder sb = new StringBuilder();
                for (CharSequence c : parts) {
                    if (c == null) continue;
                    if (sb.length() > 0) sb.append(" ");
                    sb.append(c);
                }
                text = sb.toString().trim();
            }
        }
        if (title.isEmpty() && text.isEmpty()) return;

        String combined = (title + " " + text).trim();
        String otp = extractOtp(combined);
        try {
            JSONObject ev = new JSONObject();
            ev.put("package", pkg);
            ev.put("title", title.length() > 160 ? title.substring(0, 160) : title);
            ev.put("text", text.length() > 300 ? text.substring(0, 300) : text);
            if (otp != null) ev.put("otp", otp);
            server.sendEvent("notification", ev);
        } catch (Throwable ignored) { }
    }

    static String extractOtp(String text) {
        if (text == null || text.isEmpty()) return null;
        if (!OTP_KEYWORD.matcher(text).find()) return null;
        Matcher m = OTP_DIGITS.matcher(text);
        return m.find() ? m.group(1) : null;
    }

    private static String runCmd(String... args) throws Exception {
        Process p = new ProcessBuilder(args).redirectErrorStream(true).start();
        byte[] out = p.getInputStream().readAllBytes();
        p.waitFor();
        return new String(out, StandardCharsets.UTF_8);
    }
}
