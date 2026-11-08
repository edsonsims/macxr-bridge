// SPDX-License-Identifier: MPL-2.0
//
// Link para Mac: captures a Quest passthrough camera with Camera2, JPEG-encodes each frame and
// hands it to the native client (nativePushCameraFrame) together with the camera intrinsics and
// its pose relative to the head. Needs horizonos.permission.HEADSET_CAMERA (HzOS v74+).

package com.oculus;

import android.content.Context;
import android.graphics.ImageFormat;
import android.hardware.camera2.CameraAccessException;
import android.hardware.camera2.CameraCaptureSession;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraDevice;
import android.hardware.camera2.CameraManager;
import android.hardware.camera2.CaptureRequest;
import android.hardware.camera2.params.StreamConfigurationMap;
import android.media.Image;
import android.media.ImageReader;
import android.os.Handler;
import android.os.HandlerThread;
import android.util.Log;
import android.util.Size;
import android.view.Surface;

import java.nio.ByteBuffer;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

public final class HeadsetCamera
{
    private static final String TAG = "OXRSys-Camera";
    private static final String CAMERA_PERMISSION = "horizonos.permission.HEADSET_CAMERA";

    // Meta vendor metadata keys on Quest passthrough cameras.
    private static final String KEY_SOURCE = "com.meta.extra_metadata.camera_source";
    private static final String KEY_POSITION = "com.meta.extra_metadata.position";

    private static native void nativePushCameraFrame(byte[] jpeg, int length, int width, int height,
                                                     float fx, float fy, float cx, float cy,
                                                     float px, float py, float pz,
                                                     float qx, float qy, float qz, float qw,
                                                     long timestampNs);

    private final Context context;
    private HandlerThread thread;
    private Handler handler;
    private CameraDevice device;
    private CameraCaptureSession session;
    private ImageReader reader;
    private boolean running;
    private static final int TARGET_WIDTH = 1280;
    private static final int TARGET_HEIGHT = 960;

    // Intrinsics/extrinsics of the opened camera, read once from characteristics.
    private float fx, fy, cx, cy;
    private float px, py, pz, qx, qy, qz, qw = 1f;
    private int targetFps = 10;

    public HeadsetCamera(Context context)
    {
        this.context = context;
    }

    public synchronized void start(int preferredEye, int fps)
    {
        if (running) return;
        if (context.checkSelfPermission(CAMERA_PERMISSION) != android.content.pm.PackageManager.PERMISSION_GRANTED)
        {
            Log.w(TAG, "HEADSET_CAMERA permission not granted; passthrough camera relay off");
            return;
        }
        targetFps = Math.max(1, Math.min(fps, 30));
        thread = new HandlerThread("OXRSysCamera");
        thread.start();
        handler = new Handler(thread.getLooper());
        handler.post(() -> openCamera(preferredEye));
        running = true;
    }

    public synchronized void stop()
    {
        running = false;
        if (handler != null) handler.post(this::closeCamera);
        if (thread != null)
        {
            thread.quitSafely();
            thread = null;
        }
        handler = null;
    }

    private Integer readVendorInt(CameraCharacteristics c, String name)
    {
        try
        {
            for (CameraCharacteristics.Key<?> key : c.getKeys())
            {
                if (key.getName().equals(name))
                {
                    Object v = c.get((CameraCharacteristics.Key<Object>) key);
                    if (v instanceof byte[] && ((byte[]) v).length > 0) return (int) ((byte[]) v)[0];
                    if (v instanceof Integer) return (Integer) v;
                }
            }
        }
        catch (Throwable ignored) {}
        return null;
    }

    private String pickCamera(CameraManager manager, int preferredEye) throws CameraAccessException
    {
        String firstBackWithIntrinsics = null;
        for (String id : manager.getCameraIdList())
        {
            CameraCharacteristics c = manager.getCameraCharacteristics(id);
            Integer facing = c.get(CameraCharacteristics.LENS_FACING);
            float[] intr = c.get(CameraCharacteristics.LENS_INTRINSIC_CALIBRATION);
            if (facing == null || facing != CameraCharacteristics.LENS_FACING_BACK || intr == null) continue;
            Integer source = readVendorInt(c, KEY_SOURCE);   // 0 = passthrough
            Integer position = readVendorInt(c, KEY_POSITION); // 0 = left, 1 = right
            if (firstBackWithIntrinsics == null) firstBackWithIntrinsics = id;
            boolean isPassthrough = source == null || source == 0;
            if (isPassthrough && (position == null || position == preferredEye))
            {
                Log.i(TAG, "Passthrough camera id=" + id + " source=" + source + " position=" + position);
                return id;
            }
        }
        Log.w(TAG, "No tagged passthrough camera; using first back camera with intrinsics: " + firstBackWithIntrinsics);
        return firstBackWithIntrinsics;
    }

    private Size pickSize(CameraCharacteristics c)
    {
        StreamConfigurationMap map = c.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP);
        if (map == null) return new Size(640, 480);
        Size[] sizes = map.getOutputSizes(ImageFormat.JPEG);
        if (sizes == null || sizes.length == 0) return new Size(640, 480);
        // Link para Mac: 1280x960 (4:3, the full sensor width) — sharp enough for mixed reality recordings and
        // still cheap over USB as JPEG. The inference model downsamples anyway.
        Size best = sizes[0];
        long bestScore = Long.MAX_VALUE;
        for (Size s : sizes)
        {
            long score = Math.abs((long) s.getWidth() * s.getHeight() - (long) TARGET_WIDTH * TARGET_HEIGHT) +
                         (s.getWidth() * 3 == s.getHeight() * 4 ? 0 : 1_000_000L); // prefer 4:3
            if (score < bestScore) { bestScore = score; best = s; }
        }
        return best;
    }

    private void readIntrinsicsAndPose(CameraCharacteristics c, Size size)
    {
        float[] intr = c.get(CameraCharacteristics.LENS_INTRINSIC_CALIBRATION);
        // LENS_INTRINSIC_CALIBRATION is [fx, fy, cx, cy, s] in pixels of the pre-correction active array.
        android.graphics.Rect array = null;
        if (android.os.Build.VERSION.SDK_INT >= 23)
        {
            array = c.get(CameraCharacteristics.SENSOR_INFO_PRE_CORRECTION_ACTIVE_ARRAY_SIZE);
        }
        if (array == null) array = c.get(CameraCharacteristics.SENSOR_INFO_ACTIVE_ARRAY_SIZE);
        final float streamW = size.getWidth(), streamH = size.getHeight();
        if (intr != null && intr.length >= 4 && array != null && array.width() > 0 && array.height() > 0)
        {
            // The stream is a centered aspect-fit crop of the sensor, then a uniform scale (not a stretch):
            // a 640x480 stream of a 1280x1280 sensor uses the 1280x960 band in the middle.
            float arrayW = array.width(), arrayH = array.height();
            float cropW = Math.min(arrayW, arrayH * streamW / streamH);
            float cropH = cropW * streamH / streamW;
            float cropX = (arrayW - cropW) * 0.5f, cropY = (arrayH - cropH) * 0.5f;
            float scale = streamW / cropW;
            fx = intr[0] * scale; fy = intr[1] * scale;
            cx = (intr[2] - cropX) * scale; cy = (intr[3] - cropY) * scale;
        }
        else
        {
            // Fallback: assume ~90 degree horizontal FOV.
            fx = fy = streamW * 0.5f; cx = streamW * 0.5f; cy = streamH * 0.5f;
        }
        float[] t = c.get(CameraCharacteristics.LENS_POSE_TRANSLATION);
        float[] r = c.get(CameraCharacteristics.LENS_POSE_ROTATION);
        if (t != null && t.length >= 3) { px = t[0]; py = t[1]; pz = t[2]; }
        if (r != null && r.length >= 4) { qx = r[0]; qy = r[1]; qz = r[2]; qw = r[3]; }
        Log.i(TAG, "Intrinsics (stream " + (int) streamW + "x" + (int) streamH + ") fx=" + fx + " fy=" + fy + " cx=" + cx + " cy=" + cy +
                   " pose t=(" + px + "," + py + "," + pz + ") q=(" + qx + "," + qy + "," + qz + "," + qw + ")");
    }

    // Link para Mac: the camera is refused while the headset sleeps (validateClientPermissionsLocked) and is
    // taken away when another app grabs it; keep retrying while we are supposed to be running.
    private void retryOpenLater(int preferredEye)
    {
        Handler h = handler;
        if (h == null) return;
        h.postDelayed(() -> { synchronized (HeadsetCamera.this) { if (!running || device != null) return; } openCamera(preferredEye); }, 2000);
    }

    private void openCamera(int preferredEye)
    {
        CameraManager manager = (CameraManager) context.getSystemService(Context.CAMERA_SERVICE);
        try
        {
            String id = pickCamera(manager, preferredEye);
            if (id == null) { Log.e(TAG, "No usable passthrough camera found"); return; }
            CameraCharacteristics c = manager.getCameraCharacteristics(id);
            Size size = pickSize(c);
            readIntrinsicsAndPose(c, size);
            reader = ImageReader.newInstance(size.getWidth(), size.getHeight(), ImageFormat.JPEG, 3);
            reader.setOnImageAvailableListener(this::onImage, handler);
            manager.openCamera(id, new CameraDevice.StateCallback()
            {
                @Override public void onOpened(CameraDevice cam)
                {
                    device = cam;
                    createSession(size);
                }
                @Override public void onDisconnected(CameraDevice cam) { Log.w(TAG, "Camera disconnected"); closeCamera(); retryOpenLater(preferredEye); }
                @Override public void onError(CameraDevice cam, int error) { Log.e(TAG, "Camera error " + error); closeCamera(); retryOpenLater(preferredEye); }
            }, handler);
        }
        catch (Throwable t)
        {
            Log.w(TAG, "openCamera failed (retrying in 2 s): " + t.getMessage());
            closeCamera();
            retryOpenLater(preferredEye);
        }
    }

    private void createSession(Size size)
    {
        try
        {
            List<Surface> surfaces = new ArrayList<>();
            surfaces.add(reader.getSurface());
            device.createCaptureSession(surfaces, new CameraCaptureSession.StateCallback()
            {
                @Override public void onConfigured(CameraCaptureSession s)
                {
                    session = s;
                    try
                    {
                        CaptureRequest.Builder b = device.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW);
                        b.addTarget(reader.getSurface());
                        long ns = 1_000_000_000L / targetFps;
                        b.set(CaptureRequest.SENSOR_FRAME_DURATION, ns);
                        b.set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, new android.util.Range<>(targetFps, targetFps));
                        b.set(CaptureRequest.JPEG_QUALITY, (byte) 85);
                        s.setRepeatingRequest(b.build(), null, handler);
                        Log.i(TAG, "Passthrough camera streaming " + size.getWidth() + "x" + size.getHeight() + " @" + targetFps + "fps");
                    }
                    catch (CameraAccessException e) { Log.e(TAG, "setRepeatingRequest: " + e.getMessage()); }
                }
                @Override public void onConfigureFailed(CameraCaptureSession s) { Log.e(TAG, "Capture session config failed"); }
            }, handler);
        }
        catch (Throwable t)
        {
            Log.e(TAG, "createSession failed: " + t.getMessage(), t);
        }
    }

    private void onImage(ImageReader r)
    {
        Image image = null;
        try
        {
            image = r.acquireLatestImage();
            if (image == null) return;
            ByteBuffer buffer = image.getPlanes()[0].getBuffer();
            int length = buffer.remaining();
            byte[] jpeg = new byte[length];
            buffer.get(jpeg);
            nativePushCameraFrame(jpeg, length, image.getWidth(), image.getHeight(),
                                  fx, fy, cx, cy, px, py, pz, qx, qy, qz, qw, image.getTimestamp());
        }
        catch (Throwable t)
        {
            Log.e(TAG, "onImage: " + t.getMessage());
        }
        finally
        {
            if (image != null) image.close();
        }
    }

    private void closeCamera()
    {
        try { if (session != null) { session.close(); session = null; } } catch (Throwable ignored) {}
        try { if (device != null) { device.close(); device = null; } } catch (Throwable ignored) {}
        try { if (reader != null) { reader.close(); reader = null; } } catch (Throwable ignored) {}
    }
}
