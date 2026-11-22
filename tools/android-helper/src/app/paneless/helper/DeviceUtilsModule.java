package app.paneless.helper;

import android.content.Context;
import android.hardware.Sensor;
import android.hardware.SensorEvent;
import android.hardware.SensorEventListener;
import android.hardware.SensorManager;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraManager;
import android.media.AudioManager;
import android.media.ToneGenerator;
import android.os.Build;
import android.os.VibrationEffect;
import android.os.Vibrator;

import org.json.JSONObject;

/**
 * On-demand camera orientation sensor stream (strictly active only while a Camera window is open)
 * and physical "Locate / Identify device" beacon (flashlight blinks + vibration + alarm chime).
 */
public final class DeviceUtilsModule {
    private static final int TYPE_DEVICE_ORIENTATION = 27;

    private final DaemonServer server;
    private final Context context;
    private final Object sensorLock = new Object();
    private SensorManager sensorManager;
    private SensorEventListener sensorListener;
    private int lastRotation = -1;

    public DeviceUtilsModule(DaemonServer server) {
        this.server = server;
        this.context = server.context();
        server.addCloseHook(this::stopCameraWatch);
    }

    public void register(DaemonServer server) {
        server.handle("camera_info", (req, bin) -> handleCameraInfo());
        server.handle("camera_watch", (req, bin) -> {
            boolean enabled = req.optBoolean("enabled", false);
            if (enabled) startCameraWatch();
            else stopCameraWatch();
            JSONObject res = new JSONObject();
            res.put("watching", sensorListener != null);
            return DaemonServer.Result.ok(res);
        });
        server.handle("locate", (req, bin) -> handleLocate(req));
    }

    private DaemonServer.Result handleCameraInfo() throws Exception {
        JSONObject res = new JSONObject();
        CameraManager cm = (CameraManager) context.getSystemService(Context.CAMERA_SERVICE);
        if (cm != null) {
            for (String id : cm.getCameraIdList()) {
                CameraCharacteristics cc = cm.getCameraCharacteristics(id);
                Integer facing = cc.get(CameraCharacteristics.LENS_FACING);
                Integer orient = cc.get(CameraCharacteristics.SENSOR_ORIENTATION);
                if (facing == null || orient == null) continue;
                if (facing == CameraCharacteristics.LENS_FACING_BACK && !res.has("backOrientation")) {
                    res.put("backOrientation", orient);
                } else if (facing == CameraCharacteristics.LENS_FACING_FRONT && !res.has("frontOrientation")) {
                    res.put("frontOrientation", orient);
                }
            }
        }
        return DaemonServer.Result.ok(res);
    }

    private void startCameraWatch() {
        synchronized (sensorLock) {
            if (sensorListener != null) return;
            sensorManager = (SensorManager) context.getSystemService(Context.SENSOR_SERVICE);
            if (sensorManager == null) return;
            Sensor s = sensorManager.getDefaultSensor(TYPE_DEVICE_ORIENTATION);
            boolean isDevOrient = (s != null);
            if (s == null) s = sensorManager.getDefaultSensor(Sensor.TYPE_ACCELEROMETER);
            if (s == null) return;

            lastRotation = -1;
            sensorListener = new SensorEventListener() {
                @Override
                public void onSensorChanged(SensorEvent event) {
                    if (event == null || event.values == null || event.values.length == 0) return;
                    int rot = isDevOrient ? Math.round(event.values[0]) : accelToRotation(event.values);
                    if (rot < 0 || rot > 3 || rot == lastRotation) return;
                    lastRotation = rot;
                    try {
                        JSONObject ev = new JSONObject();
                        ev.put("rotation", rot);
                        server.sendEvent("orientation", ev);
                    } catch (Throwable ignored) { }
                }

                @Override
                public void onAccuracyChanged(Sensor sensor, int accuracy) { }
            };
            sensorManager.registerListener(sensorListener, s, SensorManager.SENSOR_DELAY_NORMAL, server.handler());
        }
    }

    private static int accelToRotation(float[] v) {
        if (v.length < 3) return -1;
        float x = v[0], y = v[1], z = v[2];
        // Ignore when lying almost flat on a table.
        if (Math.abs(z) > 8.5f && Math.hypot(x, y) < 3.5f) return -1;
        if (Math.abs(x) > Math.abs(y) + 2.0f) return x > 0 ? 1 : 3;
        if (Math.abs(y) > Math.abs(x) + 2.0f) return y > 0 ? 0 : 2;
        return -1;
    }

    private void stopCameraWatch() {
        synchronized (sensorLock) {
            if (sensorListener != null && sensorManager != null) {
                try { sensorManager.unregisterListener(sensorListener); } catch (Throwable ignored) { }
            }
            sensorListener = null;
            lastRotation = -1;
        }
    }

    @SuppressWarnings("deprecation")
    private DaemonServer.Result handleLocate(JSONObject req) throws Exception {
        String mode = req.optString("mode", "all");
        int durationMs = Math.max(800, Math.min(8000, req.optInt("durationMs", 2800)));
        boolean doFlash = "all".equals(mode) || "flash".equals(mode);
        boolean doVibrate = "all".equals(mode) || "vibrate".equals(mode);
        boolean doChime = "all".equals(mode) || "chime".equals(mode);

        boolean vibrated = false;
        boolean chimed = false;
        boolean flashed = false;

        if (doVibrate) {
            try {
                Vibrator vib = (Vibrator) context.getSystemService(Context.VIBRATOR_SERVICE);
                if (vib != null && vib.hasVibrator()) {
                    long[] pattern = {0, 260, 180, 260, 180, 380};
                    if (Build.VERSION.SDK_INT >= 26) {
                        vib.vibrate(VibrationEffect.createWaveform(pattern, -1));
                    } else {
                        vib.vibrate(pattern, -1);
                    }
                    vibrated = true;
                }
            } catch (Throwable ignored) { }
        }

        if (doChime) {
            try {
                ToneGenerator tg = new ToneGenerator(AudioManager.STREAM_ALARM, 90);
                try {
                    tg.startTone(ToneGenerator.TONE_PROP_BEEP2, Math.min(durationMs, 1500));
                    chimed = true;
                    Thread.sleep(500);
                } finally {
                    tg.release();
                }
            } catch (Throwable ignored) { }
        }

        if (doFlash) {
            CameraManager cm = (CameraManager) context.getSystemService(Context.CAMERA_SERVICE);
            String torchId = null;
            if (cm != null) {
                try {
                    for (String id : cm.getCameraIdList()) {
                        CameraCharacteristics cc = cm.getCameraCharacteristics(id);
                        Boolean hasFlash = cc.get(CameraCharacteristics.FLASH_INFO_AVAILABLE);
                        Integer facing = cc.get(CameraCharacteristics.LENS_FACING);
                        if (Boolean.TRUE.equals(hasFlash) && facing != null && facing == CameraCharacteristics.LENS_FACING_BACK) {
                            torchId = id;
                            break;
                        }
                    }
                } catch (Throwable ignored) { }
            }
            if (cm != null && torchId != null) {
                int blinks = Math.max(2, Math.min(8, durationMs / 500));
                try {
                    for (int i = 0; i < blinks; i++) {
                        cm.setTorchMode(torchId, true);
                        flashed = true;
                        Thread.sleep(220);
                        cm.setTorchMode(torchId, false);
                        if (i + 1 < blinks) Thread.sleep(220);
                    }
                } catch (Throwable ignored) {
                } finally {
                    try { cm.setTorchMode(torchId, false); } catch (Throwable ignored) { }
                }
            }
        }

        JSONObject res = new JSONObject();
        res.put("flashed", flashed);
        res.put("vibrated", vibrated);
        res.put("chimed", chimed);
        return DaemonServer.Result.ok(res);
    }
}
