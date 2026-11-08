#!/usr/bin/env python3
# Link para Mac: measures the rotation of the RGB camera (Camera2, id 50) relative to the head by aligning the edges
# of the environment depth (XR_META_environment_depth, which comes in the view pose ≈ head) with the camera image
# from the same instant. Result in degrees (OpenXR view convention: pitch + = up,
# yaw + = left, roll + = counterclockwise).
#
# Usage: align-camera-depth.py depth_dump.bin camera.png
#   depth_dump.bin: the client writes the 5th depth frame of each provider start to
#     /sdcard/Android/data/net.demonixis.oxrsys.android/files/depth_dump.bin (adb pull)
#   camera.png: the LinkBridge "camera" command (.link/camera.png), with the headset held still
# Measured on 2026-11-08 (2 depth frames x 2 camera frames, grid search): pitch≈-2 yaw≈-3 roll≈-2 (±1°)
# → LinkCameraFeed.HeadFromCameraCalibrated. The formula using Camera2's LENS_POSE_ROTATION (-11°) scores much worse.
import struct, sys
import numpy as np
from PIL import Image
b=open(sys.argv[1],'rb').read()
w,h,ps,eye,near,far=struct.unpack_from('<4I2f',b,0); fov=struct.unpack_from('<4f',b,88)
d=np.frombuffer(b,dtype='<u2',offset=104).reshape(h,w).astype(np.float64)/65535
valid=(d>0)&(d<0.99999); z=np.where(valid, near/(1-np.where(valid,d,0.5)), np.nan)
tl,tr,tu,td=[np.tan(a) for a in fov]
img=np.asarray(Image.open(sys.argv[2]).convert('L')).astype(np.float64)
H,W=img.shape; f=431.4
def grad(a): gy,gx=np.gradient(a); return np.hypot(gx,gy)
gI=grad(img)
vv,uu=np.mgrid[0:H,0:W]; x0=(uu+0.5-320.6)/f; y0=-(vv+0.5-239.8)/f; z0=-np.ones_like(x0)
def score(pitch,yaw,roll):
    p,yw,r=np.radians([pitch,yaw,roll]); x,y,zc=x0,y0,z0
    x,y=x*np.cos(r)-y*np.sin(r), x*np.sin(r)+y*np.cos(r)
    y,zc=y*np.cos(p)-zc*np.sin(p), y*np.sin(p)+zc*np.cos(p)
    x,zc=x*np.cos(yw)+zc*np.sin(yw), -x*np.sin(yw)+zc*np.cos(yw)
    tx=x/(-zc); ty=y/(-zc)
    i=((tx-tl)/(tr-tl)*w).astype(int); j=((ty-td)/(tu-td)*h).astype(int)
    ok=(i>=0)&(i<w)&(j>=0)&(j<h); D=np.full((H,W),np.nan); D[ok]=z[j[ok],i[ok]]
    L=np.log(D); gD=grad(np.nan_to_num(L,nan=np.nanmedian(L))); m=~np.isnan(D)
    e=(gD>np.nanpercentile(gD[m],95))&m
    return gI[e].mean()/gI[m].mean()
p,y,r=0.,0.,0.
for step in (2.0,1.0,0.5,0.25):
    improved=True
    while improved:
        improved=False; base=score(p,y,r)
        for dp,dy,dr in ((step,0,0),(-step,0,0),(0,step,0),(0,-step,0),(0,0,step),(0,0,-step)):
            s=score(p+dp,y+dy,r+dr)
            if s>base+1e-4: p,y,r,base=p+dp,y+dy,r+dr,s; improved=True
print(f"pitch={p} yaw={y} roll={r} score={score(p,y,r):.3f} (0,0,0)={score(0,0,0):.3f} (-11,0,0)={score(-11.1,0,0):.3f}")
