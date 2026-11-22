package app.paneless.helper;

import android.content.Context;
import android.net.LocalServerSocket;
import android.net.LocalSocket;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.Looper;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.IOException;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * Single-client persistent daemon over an abstract Unix socket (forwarded via adb forward).
 * Closes the listening socket as soon as Paneless connects, holds no wakelocks, sends no keepalives,
 * and exits cleanly on disconnect.
 */
public final class DaemonServer {
    public static final int PROTOCOL_VERSION = 2;

    @FunctionalInterface
    public interface CommandHandler {
        Result handle(JSONObject req, byte[] binary) throws Exception;
    }

    public static final class Result {
        public final boolean ok;
        public final JSONObject data;
        public final byte[] binary;
        public final String error;

        private Result(boolean ok, JSONObject data, byte[] binary, String error) {
            this.ok = ok;
            this.data = data;
            this.binary = binary;
            this.error = error;
        }

        public static Result ok(JSONObject data) { return new Result(true, data, null, null); }
        public static Result ok(JSONObject data, byte[] binary) { return new Result(true, data, binary, null); }
        public static Result ok() { return new Result(true, new JSONObject(), null, null); }
        public static Result fail(String error) { return new Result(false, null, null, error); }
    }

    private final Context context;
    private final HandlerThread looperThread;
    private final Handler looperHandler;
    private final Map<String, CommandHandler> handlers = new ConcurrentHashMap<>();
    private final List<Runnable> onClose = new ArrayList<>();
    private final ExecutorService workers = Executors.newFixedThreadPool(4);
    private final AtomicBoolean closed = new AtomicBoolean(false);
    private volatile FrameChannel channel;

    public DaemonServer(Context context) {
        this.context = context;
        this.looperThread = new HandlerThread("paneless-looper");
        this.looperThread.start();
        this.looperHandler = new Handler(this.looperThread.getLooper());
        addCloseHook(() -> looperThread.quitSafely());
        handle("ping", (req, bin) -> {
            JSONObject res = new JSONObject();
            res.put("version", PROTOCOL_VERSION);
            res.put("sdk", Build.VERSION.SDK_INT);
            res.put("commands", new JSONArray(handlers.keySet()));
            return Result.ok(res);
        });
        handle("shutdown", (req, bin) -> {
            closed.set(true);
            return Result.ok();
        });
    }

    public Context context() { return context; }
    public Looper looper() { return looperThread.getLooper(); }
    public Handler handler() { return looperHandler; }

    public void handle(String cmd, CommandHandler handler) {
        handlers.put(cmd, handler);
    }

    public void addCloseHook(Runnable r) {
        synchronized (onClose) { onClose.add(r); }
    }

    /** Sends an unsolicited push event frame to Paneless. Safe from any thread. */
    public void sendEvent(String eventName, JSONObject payload, byte[] binary) {
        FrameChannel ch = this.channel;
        if (ch == null || closed.get()) return;
        try {
            JSONObject hdr = payload != null ? payload : new JSONObject();
            hdr.put("event", eventName);
            ch.write(hdr, binary);
        } catch (Throwable ignored) {
            // If the socket is dead, the read loop will tear down the daemon cleanly.
        }
    }

    public void sendEvent(String eventName, JSONObject payload) {
        sendEvent(eventName, payload, null);
    }

    public void run(String socketName) throws Exception {
        LocalServerSocket server = new LocalServerSocket(socketName);
        System.out.println("READY " + socketName);
        System.out.flush();

        LocalSocket client;
        try {
            client = server.accept();
        } finally {
            // Single-client lockdown: close the listening socket immediately after Paneless connects.
            try { server.close(); } catch (IOException ignored) { }
        }

        try {
            FrameChannel ch = new FrameChannel(client.getInputStream(), client.getOutputStream());
            this.channel = ch;

            while (!closed.get()) {
                FrameChannel.Frame frame = ch.read();
                if (frame == null) break;
                dispatch(ch, frame);
            }
        } finally {
            closed.set(true);
            runCloseHooks();
            workers.shutdownNow();
            try { client.close(); } catch (IOException ignored) { }
        }
    }

    private void dispatch(FrameChannel ch, FrameChannel.Frame frame) {
        long id = frame.header.optLong("id", 0);
        String cmd = frame.header.optString("cmd", "");
        CommandHandler h = handlers.get(cmd);
        if (h == null) {
            sendResponse(ch, id, Result.fail("Unknown command: " + cmd));
            return;
        }
        if ("shutdown".equals(cmd)) {
            sendResponse(ch, id, Result.ok());
            closed.set(true);
            return;
        }
        workers.execute(() -> {
            Result res;
            try {
                res = h.handle(frame.header, frame.binary);
            } catch (Throwable t) {
                String msg = t.getMessage();
                res = Result.fail(t.getClass().getSimpleName() + (msg != null && !msg.isEmpty() ? ": " + msg : ""));
            }
            sendResponse(ch, id, res);
        });
    }

    private void sendResponse(FrameChannel ch, long id, Result res) {
        try {
            JSONObject hdr = res.data != null ? res.data : new JSONObject();
            hdr.put("id", id);
            hdr.put("ok", res.ok);
            if (!res.ok && res.error != null) hdr.put("error", res.error);
            ch.write(hdr, res.binary);
        } catch (Throwable ignored) {
            // Read loop handles disconnect.
        }
    }

    private void runCloseHooks() {
        List<Runnable> hooks;
        synchronized (onClose) { hooks = new ArrayList<>(onClose); }
        for (Runnable r : hooks) {
            try { r.run(); } catch (Throwable ignored) { }
        }
    }
}
