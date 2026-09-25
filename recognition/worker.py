"""In-memory Data Matrix reader. Capture, decode and preview have bounded latest-frame slots."""
import base64
from collections import deque
from datetime import datetime, timezone
import json
import os
import queue
import sys
import threading
import time
import uuid
import cv2
import zxingcpp
from .ipc import read_message, Writer

def utcnow():
    return datetime.now(timezone.utc).isoformat()

def replace_latest(buffer, value):
    try:
        buffer.put_nowait(value)
    except queue.Full:
        try:
            buffer.get_nowait()
        except queue.Empty:
            pass
        buffer.put_nowait(value)

def jpeg(frame):
    ratio = min(1, 1280/frame.shape[1], 720/frame.shape[0])
    if ratio < 1:
        frame = cv2.resize(frame, (max(1, round(frame.shape[1]*ratio)), max(1, round(frame.shape[0]*ratio))), interpolation=cv2.INTER_AREA)
    ok, encoded = cv2.imencode('.jpg', frame, [cv2.IMWRITE_JPEG_QUALITY, 75])
    if not ok:
        raise RuntimeError('Preview encoding failed')
    return {'jpeg':base64.b64encode(encoded).decode('ascii'), 'width':frame.shape[1], 'height':frame.shape[0]}

def decode_frame(frame):
    started = time.perf_counter()
    codes = []
    for code in zxingcpp.read_barcodes(frame, formats=zxingcpp.BarcodeFormat.DataMatrix, try_rotate=True, try_downscale=True):
        # Validate the decoded ASCII bytes in the server. No OCR, correction or rewriting.
        codes.append({'raw':bytes(code.bytes).decode('ascii', errors='replace')})
        if len(codes) >= 64:
            break
    return codes, (time.perf_counter()-started)*1000

class Worker:
    def __init__(self):
        self.commands = queue.Queue(maxsize=64)
        self.controls = queue.Queue(maxsize=32)
        self.decode_frames = queue.Queue(maxsize=1)
        self.preview_frames = queue.Queue(maxsize=1)
        self.results = queue.Queue(maxsize=1)
        self.output_preview = queue.Queue(maxsize=1)
        self.stop = threading.Event()
        self.writer = Writer(sys.stdout.buffer)
        self.capture = None
        self.replay = None
        self.last_replay = 0
        self.generation = 0
        self.sequence = 0
        self.local_preview_enabled = True
        self.stream_id = str(uuid.uuid4())
        self.settings = {}
        self.capture_times = deque(maxlen=60)

    def status(self, state, message, **extra):
        self.controls.put({'type':'status','generation':self.generation,'state':state,'message':message, **extra})

    def run(self):
        threads = [threading.Thread(target=target, daemon=True) for target in (self.read_commands,self.output,self.decode,self.preview)]
        for thread in threads: thread.start()
        self.status('ready', 'Data Matrix worker ready; no OCR models or image storage.')
        try:
            while not self.stop.is_set():
                try:
                    self.command(self.commands.get(timeout=.001 if self.capture is not None or self.replay is not None else .05))
                except queue.Empty: pass
                except Exception as exc: self.status('error', str(exc))
                started = time.perf_counter()
                frame = None
                if self.capture is not None:
                    ok, frame = self.capture.read()
                    if not ok:
                        self.disconnect()
                        self.status('camera_error','Camera capture failed; retrying when a client needs it.')
                        continue
                elif self.replay is not None and time.monotonic()-self.last_replay >= .03:
                    frame = self.replay.copy()
                    self.last_replay = time.monotonic()
                if frame is None: continue
                read_ms = (time.perf_counter()-started)*1000
                now = time.monotonic()
                self.capture_times.append(now)
                fps = (len(self.capture_times)-1)/(now-self.capture_times[0]) if len(self.capture_times)>1 and now>self.capture_times[0] else 0
                self.sequence += 1
                meta = {'generation':self.generation,'frame_sequence':self.sequence,'captured_at':utcnow(),
                        'input_kind':'replay' if self.replay is not None else 'camera', 'capture_fps':fps, 'read_ms':read_ms}
                packet = (meta,frame)
                replace_latest(self.decode_frames,packet)
                if self.local_preview_enabled: replace_latest(self.preview_frames,packet)
        finally:
            self.disconnect()
            self.stop.set()
            for thread in threads[1:]: thread.join(timeout=2)

    def read_commands(self):
        try:
            stream = getattr(sys.stdin.buffer,'raw',sys.stdin.buffer)
            while not self.stop.is_set():
                message = read_message(stream)
                if message is None: break
                self.commands.put(message)
        except Exception as exc: print(str(exc),file=sys.stderr)
        finally: self.stop.set()

    def output(self):
        while not self.stop.is_set():
            sent = False
            # Fair turns prevent fast preview from starving scan results or status.
            for buffer in (self.controls,self.results,self.output_preview):
                try: message = buffer.get_nowait()
                except queue.Empty: continue
                if message.get('generation') != self.generation: continue
                if message['type']=='preview' and not self.local_preview_enabled: continue
                try: self.writer.send(message); sent=True
                except (BrokenPipeError,OSError): self.stop.set(); return
            if not sent: self.stop.wait(.003)

    def decode(self):
        while not self.stop.is_set():
            try: meta,frame = self.decode_frames.get(timeout=.1)
            except queue.Empty: continue
            if meta['generation']!=self.generation: continue
            try:
                codes,elapsed = decode_frame(frame)
                if meta['generation']==self.generation:
                    replace_latest(self.results,{'type':'candidate',**meta,'codes':codes,'decode_ms':elapsed})
            except Exception as exc: self.status('decode_error',str(exc))

    def preview(self):
        while not self.stop.is_set():
            try: meta,frame = self.preview_frames.get(timeout=.1)
            except queue.Empty: continue
            if meta['generation']!=self.generation or not self.local_preview_enabled: continue
            try:
                started=time.perf_counter()
                image=jpeg(frame)
                if meta['generation']==self.generation and self.local_preview_enabled:
                    replace_latest(self.output_preview,{'type':'preview',**meta,**image,'encode_ms':(time.perf_counter()-started)*1000})
            except Exception as exc: self.status('preview_error',str(exc))

    def invalidate(self, command):
        self.generation=int(command.get('generation',self.generation+1))
        for buffer in (self.decode_frames,self.preview_frames,self.results,self.output_preview):
            while True:
                try: buffer.get_nowait()
                except queue.Empty: break
        self.capture_times.clear()

    def disconnect(self):
        if self.capture is not None: self.capture.release()
        self.capture=None
        self.replay=None

    def command(self, command):
        kind=command.get('type')
        if int(command.get('generation',self.generation)) < self.generation: return
        if kind=='shutdown': self.stop.set()
        elif kind=='local_preview': self.local_preview_enabled=bool(command.get('enabled',True))
        elif kind=='connect':
            self.invalidate(command); self.disconnect()
            index=int(command.get('camera_index',0))
            self.capture=cv2.VideoCapture(index,cv2.CAP_DSHOW if os.name=='nt' else cv2.CAP_ANY)
            if not self.capture.isOpened():
                self.disconnect(); self.status('camera_error',f'Could not open camera {index}'); return
            self.settings['camera_index']=index
            capabilities=self.apply_camera_settings(command.get('settings',{}))
            self.status('connected','Camera connected',camera=self.camera_settings(),capabilities=capabilities)
        elif kind=='disconnect':
            self.invalidate(command); self.disconnect(); self.status('disconnected','Camera disconnected')
        elif kind=='enumerate_cameras':
            if self.capture is not None:
                self.status('cameras','Current camera',cameras=[{'index':self.settings.get('camera_index',0),'name':'Connected camera'}]);return
            cameras=[]
            for index in range(min(16,int(command.get('max_indices',5)))):
                capture=cv2.VideoCapture(index,cv2.CAP_DSHOW if os.name=='nt' else cv2.CAP_ANY)
                if capture.isOpened(): cameras.append({'index':index,'name':f'Camera {index}'})
                capture.release()
            self.status('cameras','Camera enumeration complete',cameras=cameras)
        elif kind=='apply_camera_settings':
            self.invalidate(command)
            capabilities=self.apply_camera_settings(command.get('settings',{}))
            self.status('camera_settings','Camera settings applied',camera=self.camera_settings(),capabilities=capabilities)
        elif kind=='read_camera_settings': self.status('camera_settings','Camera readback',camera=self.camera_settings())
        elif kind=='replay':
            self.invalidate(command);self.disconnect()
            self.replay=cv2.imread(command['path'])
            if self.replay is None: raise ValueError('Replay image could not be read')
            self.status('replay','Developer replay input')
        else: raise ValueError(f'Unknown command: {kind}')

    CAMERA_PROPERTIES = {"width":cv2.CAP_PROP_FRAME_WIDTH,"height":cv2.CAP_PROP_FRAME_HEIGHT,"fps":cv2.CAP_PROP_FPS,
                         "autofocus":cv2.CAP_PROP_AUTOFOCUS,"focus":cv2.CAP_PROP_FOCUS,"auto_exposure":cv2.CAP_PROP_AUTO_EXPOSURE,
                         "exposure":cv2.CAP_PROP_EXPOSURE,"gain":cv2.CAP_PROP_GAIN,"auto_white_balance":cv2.CAP_PROP_AUTO_WB,
                         "white_balance":cv2.CAP_PROP_WHITE_BALANCE_BLUE_U if os.name=="nt" else cv2.CAP_PROP_WB_TEMPERATURE}

    def camera_settings(self):
        if self.capture is None:
            return {}
        return {name:self.capture.get(prop) for name,prop in self.CAMERA_PROPERTIES.items()}

    def apply_camera_settings(self, settings):
        if self.capture is None:
            return {}
        values = {}
        for name, prop in self.CAMERA_PROPERTIES.items():
            before = self.capture.get(prop)
            has_request = name in settings and settings[name] is not None
            requested = float(settings[name]) if has_request else before
            # OpenCV 4.12 DirectShow setProperty uses cvRound(value)==1 for auto.
            # Its getter does not implement AUTO_EXPOSURE; report raw -1 as unavailable.
            if name == "auto_exposure" and has_request and isinstance(settings[name],bool):
                requested = 1.0 if settings[name] else 0.0
            accepted = self.capture.set(prop, requested) if has_request else False
            actual=self.capture.get(prop)
            readable=not (actual == -1 and name not in ("exposure","gain"))
            values[name] = {"requested":requested,"actual":actual,"set_accepted":accepted,
                            "supported":accepted or readable,"readback_available":readable,
                            "reason":"Applied mode cannot be read back by the OpenCV DirectShow backend." if not readable else None if accepted else "Current value readable; setter not yet verified or rejected by driver."}
        values["power_line_frequency"] = {"supported":False,"reason":"OpenCV DirectShow exposes no portable power-line-frequency control."}
        return values


if __name__=='__main__':
    Worker().run()
