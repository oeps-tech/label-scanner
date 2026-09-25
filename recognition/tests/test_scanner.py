import base64
import io
import os
from pathlib import Path
import queue
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch
import zlib
import cv2
import numpy as np
import zxingcpp
from recognition.worker import Worker, decode_frame
from recognition.ipc import Writer, read_message

def payload(q='0'):
    body=f'1*OEPSA010123*0926-TRAY-Q5R2*{q}'
    return f'{body}*{zlib.crc32(body.encode("ascii")):08X}'

def symbol(text, format=zxingcpp.BarcodeFormat.DataMatrix):
    return np.asarray(zxingcpp.write_barcode(format,text,width=280,height=280,quiet_zone=12))

class ScannerTests(unittest.TestCase):
    def test_multiple_datamatrix_no_qr_or_aruco(self):
        canvas=np.full((520,1500),255,np.uint8)
        for x,code in zip((15,515,1015),(symbol(payload()),symbol(payload('123.45')),symbol('ignore QR',zxingcpp.BarcodeFormat.QRCode))):
            canvas[40:40+code.shape[0],x:x+code.shape[1]]=code
        codes,elapsed=decode_frame(canvas)
        self.assertEqual({c['raw'] for c in codes},{payload(),payload('123.45')})
        self.assertGreater(elapsed,0)
        codes,_=decode_frame(cv2.rotate(canvas,cv2.ROTATE_90_CLOCKWISE))
        self.assertEqual({c['raw'] for c in codes},{payload(),payload('123.45')})

    def test_preview_does_not_wait_for_decode(self):
        worker=Worker(); image=np.full((480,640,3),255,np.uint8)
        blocked,finish=threading.Event(),threading.Event()
        meta={'generation':0,'frame_sequence':1,'captured_at':'test'}
        def slow(_):
            blocked.set();finish.wait(3);return [],2000
        with patch('recognition.worker.decode_frame',side_effect=slow):
            threads=[threading.Thread(target=target) for target in (worker.decode,worker.preview)]
            for thread in threads:thread.start()
            try:
                worker.decode_frames.put((meta,image));self.assertTrue(blocked.wait(2))
                for seq in range(1,5):
                    worker.preview_frames.put(({**meta,'frame_sequence':seq},image))
                    self.assertEqual(worker.output_preview.get(timeout=2)['frame_sequence'],seq)
                self.assertFalse(finish.is_set())
            finally:
                worker.stop.set();finish.set()
                for thread in threads:thread.join(3)

    def test_subprocess_replay_preview_toggle_disconnect_no_disk_outputs(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder); image=root/'input.png';cv2.imwrite(str(image),symbol(payload()))
            env={**os.environ,'OEPS_SCANNER_DATA':str(root/'no-output')}
            process=subprocess.Popen([sys.executable,'-u','-m','recognition.worker'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,env=env)
            received=queue.Queue();errors=[]
            def receive():
                while True:
                    message=read_message(process.stdout)
                    if message is None:return
                    received.put(message)
            reader=threading.Thread(target=receive,daemon=True);reader.start()
            err_reader=threading.Thread(target=lambda:errors.append(process.stderr.read()),daemon=True);err_reader.start()
            sender=Writer(process.stdin)
            def wait(predicate):
                end=time.monotonic()+8
                while time.monotonic()<end:
                    try: value=received.get(timeout=.2)
                    except queue.Empty:continue
                    if predicate(value):return value
                self.fail('Timed out: '+repr(errors))
            try:
                wait(lambda m:m.get('state')=='ready')
                sender.send({'type':'replay','generation':1,'path':str(image)})
                found=wait(lambda m:m['type']=='candidate' and m['generation']==1)
                self.assertEqual(found['codes'][0]['raw'],payload())
                self.assertNotIn('images',found)
                preview=wait(lambda m:m['type']=='preview')
                self.assertTrue(base64.b64decode(preview['jpeg']).startswith(b'\xff\xd8'))
                sender.send({'type':'local_preview','enabled':False})
                after=wait(lambda m:m['type']=='candidate' and m['frame_sequence']>preview['frame_sequence']+3)
                self.assertEqual(after['codes'][0]['raw'],payload())
                sender.send({'type':'disconnect','generation':2})
                wait(lambda m:m.get('state')=='disconnected')
                self.assertFalse((root/'no-output').exists())
                sender.send({'type':'local_preview','enabled':True})
                sender.send({'type':'replay','generation':3,'path':str(image)})
                wait(lambda m:m['type']=='preview' and m['generation']==3)
            finally:
                sender.send({'type':'shutdown'})
                process.wait(timeout=5);reader.join(2);err_reader.join(2)
                process.stdin.close();process.stdout.close();process.stderr.close()
            self.assertEqual(process.returncode,0,errors)

    def test_ipc_roundtrip(self):
        stream=io.BytesIO();Writer(stream).send({'type':'connect','generation':5});stream.seek(0)
        self.assertEqual(read_message(stream),{'type':'connect','generation':5})

if __name__=='__main__': unittest.main()
