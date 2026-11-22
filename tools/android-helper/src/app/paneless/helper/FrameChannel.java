package app.paneless.helper;

import org.json.JSONObject;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.EOFException;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;

/**
 * Length-prefixed JSON + optional binary payload framing over a single bidirectional stream.
 * Wire layout: [int32 headerLen][UTF-8 JSON header][int32 binLen][raw binary bytes].
 */
public final class FrameChannel {
    public static final int MAX_HEADER = 16 * 1024 * 1024;
    public static final int MAX_BINARY = 64 * 1024 * 1024;

    public static final class Frame {
        public final JSONObject header;
        public final byte[] binary;

        public Frame(JSONObject header, byte[] binary) {
            this.header = header;
            this.binary = binary;
        }
    }

    private final DataInputStream in;
    private final DataOutputStream out;
    private final Object writeLock = new Object();

    public FrameChannel(InputStream input, OutputStream output) {
        this.in = new DataInputStream(new BufferedInputStream(input, 16384));
        this.out = new DataOutputStream(new BufferedOutputStream(output, 16384));
    }

    /** Reads the next frame, or returns null on clean EOF. */
    public Frame read() throws Exception {
        int headerLen;
        try {
            headerLen = in.readInt();
        } catch (EOFException eof) {
            return null;
        }
        if (headerLen <= 0 || headerLen > MAX_HEADER) {
            throw new IOException("Invalid frame header length: " + headerLen);
        }
        byte[] headerBytes = new byte[headerLen];
        in.readFully(headerBytes);

        int binLen = in.readInt();
        if (binLen < 0 || binLen > MAX_BINARY) {
            throw new IOException("Invalid frame binary length: " + binLen);
        }
        byte[] bin = binLen == 0 ? new byte[0] : new byte[binLen];
        if (binLen > 0) {
            in.readFully(bin);
        }
        JSONObject json = new JSONObject(new String(headerBytes, StandardCharsets.UTF_8));
        return new Frame(json, bin);
    }

    public void write(JSONObject header, byte[] binary) throws IOException {
        byte[] headerBytes = header.toString().getBytes(StandardCharsets.UTF_8);
        byte[] bin = binary == null ? new byte[0] : binary;
        synchronized (writeLock) {
            out.writeInt(headerBytes.length);
            out.write(headerBytes);
            out.writeInt(bin.length);
            if (bin.length > 0) {
                out.write(bin);
            }
            out.flush();
        }
    }
}
