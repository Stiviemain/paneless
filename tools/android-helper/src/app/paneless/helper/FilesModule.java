package app.paneless.helper;

import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Matrix;
import android.media.ExifInterface;
import android.media.ThumbnailUtils;
import android.os.Build;
import android.os.FileObserver;
import android.util.Size;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.RandomAccessFile;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Comparator;
import java.util.List;
import java.util.Locale;

/**
 * Fast on-device file browser, JPEG/video thumbnail generator, chunked binary transfer,
 * and kernel inotify FileObserver for newly taken photos & screenshots (zero polling).
 */
public final class FilesModule {
    private static final int MAX_CHUNK = 4 * 1024 * 1024;

    private final DaemonServer server;
    private final Object watchLock = new Object();
    private final List<FileObserver> observers = new ArrayList<>();

    public FilesModule(DaemonServer server) {
        this.server = server;
        server.addCloseHook(this::stopMediaWatch);
    }

    public void register(DaemonServer server) {
        server.handle("files_ls", (req, bin) -> handleList(req));
        server.handle("files_thumb", (req, bin) -> handleThumb(req));
        server.handle("files_read", (req, bin) -> handleRead(req));
        server.handle("files_write", (req, bin) -> handleWrite(req, bin));
        server.handle("files_delete", (req, bin) -> handleDelete(req));
        server.handle("files_mkdir", (req, bin) -> handleMkdir(req));
        server.handle("media_watch", (req, bin) -> {
            boolean enabled = req.optBoolean("enabled", false);
            if (enabled) startMediaWatch();
            else stopMediaWatch();
            JSONObject res = new JSONObject();
            res.put("watching", !observers.isEmpty());
            return DaemonServer.Result.ok(res);
        });
    }

    private DaemonServer.Result handleList(JSONObject req) throws Exception {
        String path = req.optString("path", "/sdcard");
        if (path.isEmpty()) path = "/sdcard";
        boolean includeHidden = req.optBoolean("includeHidden", false);
        String sort = req.optString("sort", "name");
        int limit = Math.max(10, Math.min(2000, req.optInt("limit", 500)));

        File dir = new File(path);
        if (!dir.exists() || !dir.isDirectory()) {
            return DaemonServer.Result.fail("Folder not found: " + path);
        }
        File[] raw = dir.listFiles();
        if (raw == null) {
            return DaemonServer.Result.fail("Cannot read folder: " + path);
        }

        List<File> list = new ArrayList<>(raw.length);
        for (File f : raw) {
            String name = f.getName();
            if (!includeHidden && name.startsWith(".")) continue;
            list.add(f);
        }

        if ("date".equals(sort)) {
            list.sort((a, b) -> {
                if (a.isDirectory() != b.isDirectory()) return a.isDirectory() ? -1 : 1;
                return Long.compare(b.lastModified(), a.lastModified());
            });
        } else {
            list.sort((a, b) -> {
                if (a.isDirectory() != b.isDirectory()) return a.isDirectory() ? -1 : 1;
                return a.getName().compareToIgnoreCase(b.getName());
            });
        }

        JSONArray arr = new JSONArray();
        int count = Math.min(list.size(), limit);
        for (int i = 0; i < count; i++) {
            File f = list.get(i);
            JSONObject o = new JSONObject();
            o.put("name", f.getName());
            o.put("path", f.getAbsolutePath());
            o.put("isDir", f.isDirectory());
            o.put("size", f.isDirectory() ? 0 : f.length());
            o.put("modifiedMs", f.lastModified());
            o.put("kind", kindOf(f));
            arr.put(o);
        }

        JSONObject res = new JSONObject();
        res.put("path", dir.getAbsolutePath());
        res.put("total", list.size());
        res.put("entries", arr);
        return DaemonServer.Result.ok(res);
    }

    private DaemonServer.Result handleThumb(JSONObject req) throws Exception {
        String path = req.optString("path", "");
        int maxSize = Math.max(48, Math.min(640, req.optInt("maxSize", 240)));
        File file = new File(path);
        if (!file.exists() || !file.isFile()) {
            return DaemonServer.Result.fail("File not found: " + path);
        }

        String kind = kindOf(file);
        Bitmap bmp = null;
        if ("image".equals(kind)) {
            bmp = decodeScaledImage(file, maxSize);
        } else if ("video".equals(kind)) {
            bmp = decodeVideoThumb(file, maxSize);
        } else {
            return DaemonServer.Result.fail("Thumbnails are only supported for images and videos.");
        }

        if (bmp == null) return DaemonServer.Result.fail("Could not decode thumbnail.");
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        try {
            bmp.compress(Bitmap.CompressFormat.JPEG, 80, out);
        } finally {
            bmp.recycle();
        }
        JSONObject res = new JSONObject();
        res.put("mime", "image/jpeg");
        return DaemonServer.Result.ok(res, out.toByteArray());
    }

    private static Bitmap decodeScaledImage(File file, int maxSize) {
        BitmapFactory.Options opts = new BitmapFactory.Options();
        opts.inJustDecodeBounds = true;
        BitmapFactory.decodeFile(file.getAbsolutePath(), opts);
        if (opts.outWidth <= 0 || opts.outHeight <= 0) return null;

        int longest = Math.max(opts.outWidth, opts.outHeight);
        int sample = 1;
        while (longest / (sample * 2) >= maxSize) sample *= 2;
        opts.inJustDecodeBounds = false;
        opts.inSampleSize = sample;
        Bitmap bmp = BitmapFactory.decodeFile(file.getAbsolutePath(), opts);
        if (bmp == null) return null;

        int degrees = exifDegrees(file);
        int w = bmp.getWidth();
        int h = bmp.getHeight();
        float scale = Math.min(1f, (float) maxSize / Math.max(w, h));
        if (degrees != 0 || scale < 0.99f) {
            Matrix m = new Matrix();
            if (scale < 0.99f) m.postScale(scale, scale);
            if (degrees != 0) m.postRotate(degrees);
            Bitmap rotated = Bitmap.createBitmap(bmp, 0, 0, w, h, m, true);
            if (rotated != bmp) bmp.recycle();
            bmp = rotated;
        }
        return bmp;
    }

    private static int exifDegrees(File file) {
        try {
            ExifInterface exif = new ExifInterface(file.getAbsolutePath());
            int o = exif.getAttributeInt(ExifInterface.TAG_ORIENTATION, ExifInterface.ORIENTATION_NORMAL);
            switch (o) {
                case ExifInterface.ORIENTATION_ROTATE_90: return 90;
                case ExifInterface.ORIENTATION_ROTATE_180: return 180;
                case ExifInterface.ORIENTATION_ROTATE_270: return 270;
                default: return 0;
            }
        } catch (Throwable ignored) {
            return 0;
        }
    }

    @SuppressWarnings("deprecation")
    private static Bitmap decodeVideoThumb(File file, int maxSize) {
        try {
            if (Build.VERSION.SDK_INT >= 29) {
                return ThumbnailUtils.createVideoThumbnail(file, new Size(maxSize, maxSize), null);
            }
            return ThumbnailUtils.createVideoThumbnail(file.getAbsolutePath(), android.provider.MediaStore.Video.Thumbnails.MINI_KIND);
        } catch (Throwable ignored) {
            return null;
        }
    }

    private DaemonServer.Result handleRead(JSONObject req) throws Exception {
        String path = req.optString("path", "");
        long offset = Math.max(0, req.optLong("offset", 0));
        int length = Math.max(1, Math.min(MAX_CHUNK, req.optInt("length", 1024 * 1024)));

        File file = new File(path);
        if (!file.exists() || !file.isFile()) return DaemonServer.Result.fail("File not found: " + path);
        long total = file.length();
        if (offset >= total) {
            JSONObject res = new JSONObject();
            res.put("totalSize", total);
            res.put("offset", offset);
            res.put("eof", true);
            return DaemonServer.Result.ok(res, new byte[0]);
        }

        int toRead = (int) Math.min(length, total - offset);
        byte[] buf = new byte[toRead];
        int readTotal = 0;
        try (RandomAccessFile raf = new RandomAccessFile(file, "r")) {
            raf.seek(offset);
            while (readTotal < toRead) {
                int n = raf.read(buf, readTotal, toRead - readTotal);
                if (n < 0) break;
                readTotal += n;
            }
        }
        if (readTotal < buf.length) buf = Arrays.copyOf(buf, readTotal);

        JSONObject res = new JSONObject();
        res.put("totalSize", total);
        res.put("offset", offset);
        res.put("eof", offset + readTotal >= total);
        return DaemonServer.Result.ok(res, buf);
    }

    private DaemonServer.Result handleWrite(JSONObject req, byte[] bin) throws Exception {
        String path = req.optString("path", "");
        long offset = Math.max(0, req.optLong("offset", 0));
        boolean truncate = req.optBoolean("truncate", offset == 0);
        File file = new File(path);
        File parent = file.getParentFile();
        if (parent != null && !parent.exists()) parent.mkdirs();

        if (truncate && offset == 0) {
            try (FileOutputStream fos = new FileOutputStream(file, false)) {
                if (bin != null && bin.length > 0) fos.write(bin);
            }
        } else {
            try (RandomAccessFile raf = new RandomAccessFile(file, "rw")) {
                raf.seek(offset);
                if (bin != null && bin.length > 0) raf.write(bin);
            }
        }
        JSONObject res = new JSONObject();
        res.put("written", bin != null ? bin.length : 0);
        res.put("size", file.length());
        return DaemonServer.Result.ok(res);
    }

    private DaemonServer.Result handleDelete(JSONObject req) throws Exception {
        String path = req.optString("path", "");
        File file = new File(path);
        if (!file.exists()) return DaemonServer.Result.fail("Not found: " + path);
        if (!deleteRecursive(file)) return DaemonServer.Result.fail("Could not delete: " + path);
        return DaemonServer.Result.ok();
    }

    private static boolean deleteRecursive(File f) {
        if (f.isDirectory()) {
            File[] children = f.listFiles();
            if (children != null) {
                for (File c : children) deleteRecursive(c);
            }
        }
        return f.delete();
    }

    private DaemonServer.Result handleMkdir(JSONObject req) throws Exception {
        String path = req.optString("path", "");
        File dir = new File(path);
        if (!dir.exists() && !dir.mkdirs()) return DaemonServer.Result.fail("Could not create folder: " + path);
        return DaemonServer.Result.ok();
    }

    private void startMediaWatch() {
        synchronized (watchLock) {
            if (!observers.isEmpty()) return;
            watchDir("/sdcard/DCIM/Camera", "photo");
            watchDir("/sdcard/Pictures/Screenshots", "screenshot");
            watchDir("/sdcard/DCIM/Screenshots", "screenshot");
        }
    }

    @SuppressWarnings("deprecation")
    private void watchDir(String dirPath, String kind) {
        File dir = new File(dirPath);
        if (!dir.exists() || !dir.isDirectory()) return;
        int mask = FileObserver.CLOSE_WRITE | FileObserver.MOVED_TO;
        FileObserver obs = new FileObserver(dir.getAbsolutePath(), mask) {
            @Override
            public void onEvent(int event, String fileName) {
                if (fileName == null || fileName.isEmpty() || fileName.startsWith(".") || fileName.endsWith(".tmp")) return;
                if (fileName.contains(".pending-")) return;
                File full = new File(dir, fileName);
                if (!full.isFile() || full.length() == 0) return;
                String fk = kindOf(full);
                if (!"image".equals(fk) && !"video".equals(fk)) return;
                try {
                    JSONObject ev = new JSONObject();
                    ev.put("path", full.getAbsolutePath());
                    ev.put("name", fileName);
                    ev.put("size", full.length());
                    ev.put("kind", kind);
                    server.sendEvent("media_added", ev);
                } catch (Throwable ignored) { }
            }
        };
        obs.startWatching();
        observers.add(obs);
    }

    private void stopMediaWatch() {
        synchronized (watchLock) {
            for (FileObserver obs : observers) {
                try { obs.stopWatching(); } catch (Throwable ignored) { }
            }
            observers.clear();
        }
    }

    private static String kindOf(File f) {
        if (f.isDirectory()) return "dir";
        String n = f.getName().toLowerCase(Locale.ROOT);
        int dot = n.lastIndexOf('.');
        String ext = dot >= 0 ? n.substring(dot + 1) : "";
        switch (ext) {
            case "jpg": case "jpeg": case "png": case "webp": case "gif": case "heic": case "heif": case "bmp":
                return "image";
            case "mp4": case "mkv": case "mov": case "webm": case "3gp": case "avi":
                return "video";
            case "apk": case "apkm": case "xapk":
                return "apk";
            case "mp3": case "m4a": case "ogg": case "wav": case "flac": case "opus":
                return "audio";
            case "pdf": case "txt": case "md": case "json": case "xml": case "csv": case "log": case "zip":
                return "doc";
            default:
                return "file";
        }
    }
}
