package app.paneless.helper;

import android.accessibilityservice.AccessibilityServiceInfo;
import android.app.UiAutomation;
import android.graphics.Rect;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.util.SparseArray;
import android.view.accessibility.AccessibilityEvent;
import android.view.accessibility.AccessibilityNodeInfo;
import android.view.accessibility.AccessibilityWindowInfo;

import org.json.JSONArray;
import org.json.JSONObject;

import java.lang.reflect.Constructor;
import java.lang.reflect.Method;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * Lazy, battery-friendly UiAutomation bridge for semantic UI tree reading and element actions.
 * Attaches only on demand with FLAG_DONT_SUPPRESS_ACCESSIBILITY_SERVICES and auto-detaches
 * after 90 s of inactivity (unless explicitly pinned by an opt-in event watcher).
 */
public final class UiModule {
    private static final long IDLE_DETACH_MS = 90_000L;
    private static final int FLAG_DONT_SUPPRESS = 1;

    @FunctionalInterface
    public interface NotificationEventCallback {
        void onNotification(AccessibilityEvent event);
    }

    private final DaemonServer server;
    private final Object lock = new Object();
    private UiAutomation ui;
    private boolean pinned;
    private volatile NotificationEventCallback notificationCallback;
    private final Runnable detachTask = this::detachIfIdle;
    private long lastActivityMs;

    // Last snapshot of interactive/meaningful nodes per display so ui_action can reference nodeId directly.
    private final Map<Integer, List<AccessibilityNodeInfo>> lastNodes = new ConcurrentHashMap<>();

    public UiModule(DaemonServer server) {
        this.server = server;
        server.addCloseHook(this::forceDetach);
    }

    public void register(DaemonServer server) {
        server.handle("ui_tree", (req, bin) -> handleTree(req));
        server.handle("ui_action", (req, bin) -> handleAction(req));
        server.handle("ui_detach", (req, bin) -> {
            if (!pinned) forceDetach();
            JSONObject res = new JSONObject();
            res.put("detached", ui == null);
            return DaemonServer.Result.ok(res);
        });
    }

    /** Called by ControlsModule when opt-in notification sniffing is toggled. */
    public void setNotificationCallback(NotificationEventCallback cb) throws Exception {
        synchronized (lock) {
            this.notificationCallback = cb;
            this.pinned = (cb != null);
            if (cb != null) {
                ensureConnectedLocked();
            } else {
                scheduleIdleDetachLocked();
            }
        }
    }

    private UiAutomation ensureConnected() throws Exception {
        synchronized (lock) {
            UiAutomation a = ensureConnectedLocked();
            lastActivityMs = System.currentTimeMillis();
            scheduleIdleDetachLocked();
            return a;
        }
    }

    private UiAutomation ensureConnectedLocked() throws Exception {
        if (ui != null) return ui;
        Class<?> connClass = Class.forName("android.app.UiAutomationConnection");
        Object conn = connClass.getDeclaredConstructor().newInstance();
        Class<?> iConnClass = Class.forName("android.app.IUiAutomationConnection");
        Constructor<UiAutomation> ctor = UiAutomation.class.getDeclaredConstructor(Looper.class, iConnClass);
        ctor.setAccessible(true);
        UiAutomation instance = ctor.newInstance(server.looper(), conn);

        boolean connected = false;
        try {
            Method connectWithFlags = UiAutomation.class.getDeclaredMethod("connect", int.class);
            connectWithFlags.setAccessible(true);
            connectWithFlags.invoke(instance, FLAG_DONT_SUPPRESS);
            connected = true;
        } catch (NoSuchMethodException ignored) { }
        if (!connected) {
            Method connect = UiAutomation.class.getDeclaredMethod("connect");
            connect.setAccessible(true);
            connect.invoke(instance);
        }

        try {
            AccessibilityServiceInfo info = instance.getServiceInfo();
            if (info != null) {
                info.flags |= AccessibilityServiceInfo.FLAG_RETRIEVE_INTERACTIVE_WINDOWS
                        | AccessibilityServiceInfo.FLAG_REPORT_VIEW_IDS
                        | AccessibilityServiceInfo.FLAG_INCLUDE_NOT_IMPORTANT_VIEWS;
                instance.setServiceInfo(info);
            }
        } catch (Throwable ignored) { }

        instance.setOnAccessibilityEventListener(event -> {
            if (event == null) return;
            if (event.getEventType() == AccessibilityEvent.TYPE_NOTIFICATION_STATE_CHANGED) {
                NotificationEventCallback cb = this.notificationCallback;
                if (cb != null) {
                    try { cb.onNotification(event); } catch (Throwable ignored) { }
                }
            }
        });

        this.ui = instance;
        return instance;
    }

    private void scheduleIdleDetachLocked() {
        Handler h = server.handler();
        h.removeCallbacks(detachTask);
        if (!pinned) {
            h.postDelayed(detachTask, IDLE_DETACH_MS);
        }
    }

    private void detachIfIdle() {
        synchronized (lock) {
            if (pinned || ui == null) return;
            if (System.currentTimeMillis() - lastActivityMs >= IDLE_DETACH_MS - 1000) {
                forceDetachLocked();
            } else {
                scheduleIdleDetachLocked();
            }
        }
    }

    private void forceDetach() {
        synchronized (lock) {
            pinned = false;
            notificationCallback = null;
            forceDetachLocked();
        }
    }

    private void forceDetachLocked() {
        server.handler().removeCallbacks(detachTask);
        clearCachedNodes();
        if (ui != null) {
            try {
                Method disconnect = UiAutomation.class.getDeclaredMethod("disconnect");
                disconnect.setAccessible(true);
                disconnect.invoke(ui);
            } catch (Throwable ignored) { }
            ui = null;
        }
    }

    private void clearCachedNodes() {
        lastNodes.clear();
    }

    private List<AccessibilityNodeInfo> getRoots(UiAutomation a, int displayId) {
        List<AccessibilityNodeInfo> roots = new ArrayList<>();
        if (Build.VERSION.SDK_INT >= 30) {
            try {
                SparseArray<List<AccessibilityWindowInfo>> byDisplay = a.getWindowsOnAllDisplays();
                List<AccessibilityWindowInfo> windows = byDisplay != null ? byDisplay.get(displayId) : null;
                if (windows != null) {
                    for (AccessibilityWindowInfo w : windows) {
                        if (w == null) continue;
                        AccessibilityNodeInfo root = w.getRoot();
                        if (root != null) roots.add(root);
                    }
                }
            } catch (Throwable ignored) { }
        }
        if (roots.isEmpty() && displayId == 0) {
            try {
                List<AccessibilityWindowInfo> windows = a.getWindows();
                if (windows != null) {
                    for (AccessibilityWindowInfo w : windows) {
                        if (w == null) continue;
                        AccessibilityNodeInfo root = w.getRoot();
                        if (root != null) roots.add(root);
                    }
                }
            } catch (Throwable ignored) { }
        }
        if (roots.isEmpty() && displayId == 0) {
            AccessibilityNodeInfo active = a.getRootInActiveWindow();
            if (active != null) roots.add(active);
        }
        return roots;
    }

    private DaemonServer.Result handleTree(JSONObject req) throws Exception {
        int displayId = req.optInt("displayId", 0);
        int maxDepth = Math.max(1, Math.min(32, req.optInt("maxDepth", 18)));
        int maxNodes = Math.max(10, Math.min(800, req.optInt("maxNodes", 300)));
        boolean interactiveOnly = req.optBoolean("interactiveOnly", false);

        UiAutomation a = ensureConnected();
        List<AccessibilityNodeInfo> roots = getRoots(a, displayId);
        List<AccessibilityNodeInfo> snapshot = new ArrayList<>();
        JSONArray nodesJson = new JSONArray();
        AtomicInteger counter = new AtomicInteger(0);
        String activePkg = "";

        for (AccessibilityNodeInfo root : roots) {
            if (activePkg.isEmpty() && root.getPackageName() != null) {
                activePkg = root.getPackageName().toString();
            }
            collectNodes(root, 0, maxDepth, maxNodes, interactiveOnly, counter, snapshot, nodesJson);
            if (counter.get() >= maxNodes) break;
        }

        lastNodes.put(displayId, snapshot);

        JSONObject res = new JSONObject();
        res.put("displayId", displayId);
        res.put("package", activePkg);
        res.put("count", nodesJson.length());
        res.put("truncated", counter.get() >= maxNodes);
        res.put("nodes", nodesJson);
        return DaemonServer.Result.ok(res);
    }

    private void collectNodes(
            AccessibilityNodeInfo node, int depth, int maxDepth, int maxNodes,
            boolean interactiveOnly, AtomicInteger counter,
            List<AccessibilityNodeInfo> snapshot, JSONArray out) throws Exception {
        if (node == null || depth > maxDepth || counter.get() >= maxNodes) return;
        if (!node.isVisibleToUser()) return;

        boolean clickable = node.isClickable();
        boolean longClickable = node.isLongClickable();
        boolean editable = node.isEditable();
        boolean scrollable = node.isScrollable();
        boolean checkable = node.isCheckable();
        boolean password = node.isPassword();

        String text = node.getText() != null ? node.getText().toString().trim() : "";
        if (password && !text.isEmpty()) text = "••••••••";
        String desc = node.getContentDescription() != null ? node.getContentDescription().toString().trim() : "";
        String resId = node.getViewIdResourceName() != null ? node.getViewIdResourceName() : "";

        boolean isInteractive = clickable || longClickable || editable || scrollable || checkable;
        boolean hasContent = !text.isEmpty() || !desc.isEmpty();
        boolean keep = interactiveOnly ? isInteractive : (isInteractive || hasContent);

        if (keep) {
            Rect bounds = new Rect();
            node.getBoundsInScreen(bounds);
            if (bounds.width() > 0 && bounds.height() > 0) {
                int id = counter.incrementAndGet();
                snapshot.add(node);
                out.put(nodeToJson(id, node, text, desc, resId, bounds));
            }
        }

        int childCount = node.getChildCount();
        for (int i = 0; i < childCount && counter.get() < maxNodes; i++) {
            AccessibilityNodeInfo child = node.getChild(i);
            if (child != null) {
                collectNodes(child, depth + 1, maxDepth, maxNodes, interactiveOnly, counter, snapshot, out);
            }
        }
    }

    private static JSONObject nodeToJson(int id, AccessibilityNodeInfo n, String text, String desc, String resId, Rect b) throws Exception {
        JSONObject o = new JSONObject();
        o.put("id", id);
        if (n.getClassName() != null) {
            String cls = n.getClassName().toString();
            int dot = cls.lastIndexOf('.');
            o.put("class", dot >= 0 ? cls.substring(dot + 1) : cls);
        }
        if (!text.isEmpty()) o.put("text", text.length() > 300 ? text.substring(0, 300) + "…" : text);
        if (!desc.isEmpty()) o.put("desc", desc.length() > 200 ? desc.substring(0, 200) + "…" : desc);
        if (!resId.isEmpty()) {
            int slash = resId.indexOf(":id/");
            o.put("resId", slash >= 0 ? resId.substring(slash + 4) : resId);
        }
        JSONArray rect = new JSONArray();
        rect.put(b.left); rect.put(b.top); rect.put(b.right); rect.put(b.bottom);
        o.put("bounds", rect);

        if (n.isClickable()) o.put("clickable", true);
        if (n.isLongClickable()) o.put("longClickable", true);
        if (n.isEditable()) o.put("editable", true);
        if (n.isScrollable()) o.put("scrollable", true);
        if (n.isCheckable()) o.put("checkable", true);
        if (n.isChecked()) o.put("checked", true);
        if (n.isFocused()) o.put("focused", true);
        if (n.isSelected()) o.put("selected", true);
        if (!n.isEnabled()) o.put("enabled", false);
        if (n.isPassword()) o.put("password", true);
        return o;
    }

    private DaemonServer.Result handleAction(JSONObject req) throws Exception {
        int displayId = req.optInt("displayId", 0);
        String action = req.optString("action", "click").toLowerCase(Locale.ROOT);
        int nodeId = req.optInt("nodeId", 0);
        String matchText = req.optString("text", "");
        String matchDesc = req.optString("desc", "");
        String matchResId = req.optString("resId", "");
        int matchIndex = Math.max(0, req.optInt("index", 0));
        String value = req.optString("value", "");

        UiAutomation a = ensureConnected();
        AccessibilityNodeInfo target = null;

        if (nodeId > 0) {
            List<AccessibilityNodeInfo> snap = lastNodes.get(displayId);
            if (snap != null && nodeId <= snap.size()) {
                target = snap.get(nodeId - 1);
                try { target.refresh(); } catch (Throwable ignored) { }
            }
        }

        if (target == null && (!matchText.isEmpty() || !matchDesc.isEmpty() || !matchResId.isEmpty())) {
            List<AccessibilityNodeInfo> roots = getRoots(a, displayId);
            List<AccessibilityNodeInfo> matches = new ArrayList<>();
            for (AccessibilityNodeInfo root : roots) {
                findMatchingNodes(root, matchText, matchDesc, matchResId, 0, matches);
            }
            if (matchIndex < matches.size()) {
                target = matches.get(matchIndex);
            }
        }

        if (target == null) {
            return DaemonServer.Result.fail("No matching UI element found. Call read_ui_tree first to inspect current elements.");
        }

        boolean ok;
        AccessibilityNodeInfo acted = target;
        switch (action) {
            case "click": {
                AccessibilityNodeInfo clickable = findAncestor(target, AccessibilityNodeInfo::isClickable);
                if (clickable != null) acted = clickable;
                ok = acted.performAction(AccessibilityNodeInfo.ACTION_CLICK);
                break;
            }
            case "long_click": {
                AccessibilityNodeInfo lc = findAncestor(target, AccessibilityNodeInfo::isLongClickable);
                if (lc != null) acted = lc;
                ok = acted.performAction(AccessibilityNodeInfo.ACTION_LONG_CLICK);
                break;
            }
            case "set_text": {
                AccessibilityNodeInfo ed = findAncestor(target, AccessibilityNodeInfo::isEditable);
                if (ed != null) acted = ed;
                acted.performAction(AccessibilityNodeInfo.ACTION_FOCUS);
                Bundle args = new Bundle();
                args.putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, value);
                ok = acted.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, args);
                break;
            }
            case "scroll_forward": {
                AccessibilityNodeInfo sc = findAncestor(target, AccessibilityNodeInfo::isScrollable);
                if (sc != null) acted = sc;
                ok = acted.performAction(AccessibilityNodeInfo.ACTION_SCROLL_FORWARD);
                break;
            }
            case "scroll_backward": {
                AccessibilityNodeInfo sc = findAncestor(target, AccessibilityNodeInfo::isScrollable);
                if (sc != null) acted = sc;
                ok = acted.performAction(AccessibilityNodeInfo.ACTION_SCROLL_BACKWARD);
                break;
            }
            case "focus":
                ok = target.performAction(AccessibilityNodeInfo.ACTION_FOCUS);
                break;
            case "clear_focus":
                ok = target.performAction(AccessibilityNodeInfo.ACTION_CLEAR_FOCUS);
                break;
            default:
                return DaemonServer.Result.fail("Unknown ui_action: " + action);
        }

        Rect b = new Rect();
        acted.getBoundsInScreen(b);
        JSONObject res = new JSONObject();
        res.put("performed", ok);
        res.put("action", action);
        res.put("bounds", new JSONArray(new int[]{b.left, b.top, b.right, b.bottom}));
        if (!ok) {
            return DaemonServer.Result.fail("Element refused action '" + action + "' (bounds " + b.left + "," + b.top + "," + b.right + "," + b.bottom + ").");
        }
        return DaemonServer.Result.ok(res);
    }

    @FunctionalInterface
    private interface NodePred {
        boolean test(AccessibilityNodeInfo n);
    }

    private static AccessibilityNodeInfo findAncestor(AccessibilityNodeInfo node, NodePred pred) {
        AccessibilityNodeInfo cur = node;
        for (int i = 0; i < 6 && cur != null; i++) {
            if (pred.test(cur)) return cur;
            cur = cur.getParent();
        }
        return null;
    }

    private static void findMatchingNodes(
            AccessibilityNodeInfo node, String matchText, String matchDesc, String matchResId,
            int depth, List<AccessibilityNodeInfo> out) {
        if (node == null || depth > 24 || out.size() >= 32) return;
        if (!node.isVisibleToUser()) return;

        boolean ok = true;
        if (!matchText.isEmpty()) {
            String t = node.getText() != null ? node.getText().toString() : "";
            ok = t.toLowerCase(Locale.ROOT).contains(matchText.toLowerCase(Locale.ROOT));
        }
        if (ok && !matchDesc.isEmpty()) {
            String d = node.getContentDescription() != null ? node.getContentDescription().toString() : "";
            ok = d.toLowerCase(Locale.ROOT).contains(matchDesc.toLowerCase(Locale.ROOT));
        }
        if (ok && !matchResId.isEmpty()) {
            String r = node.getViewIdResourceName() != null ? node.getViewIdResourceName() : "";
            ok = r.equalsIgnoreCase(matchResId) || r.endsWith(":id/" + matchResId);
        }
        if (ok) out.add(node);

        int count = node.getChildCount();
        for (int i = 0; i < count && out.size() < 32; i++) {
            findMatchingNodes(node.getChild(i), matchText, matchDesc, matchResId, depth + 1, out);
        }
    }
}
