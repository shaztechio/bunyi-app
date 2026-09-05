# Copyright 2026 Shazron Abdullah and Bunyi contributors
#
# Licensed under the Apache License, Version 2.0 (the "License");
# you may not use this file except in compliance with the License.
# You may obtain a copy of the License at
#
#     http://www.apache.org/licenses/LICENSE-2.0
#
# Unless required by applicable law or agreed to in writing, software
# distributed under the License is distributed on an "AS IS" BASIS,
# WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
# See the License for the specific language governing permissions and
# limitations under the License.

import argparse, ctypes as C, os, pathlib, subprocess, tempfile, time
import gi
gi.require_version('Atspi','2.0')
from gi.repository import Atspi, GLib
parser=argparse.ArgumentParser(description='Check real Bunyi AT-SPI focus events without pre-reading controls.')
parser.add_argument('app', type=pathlib.Path)
parser.add_argument('--log', type=pathlib.Path, default=pathlib.Path('atspi-app.log'))
args=parser.parse_args()
app=args.app.resolve(strict=True)
phase='startup'
events=[]
def on_event(e, *args):
    if 'focused' in e.type:
        events.append((phase,e.type,e.detail1,e.source.get_name()))
        print('EVENT',events[-1],flush=True)
listener=Atspi.EventListener.new(on_event)
listener.register('object:state-changed:focused')
def pump(seconds):
    end=time.monotonic()+seconds
    while time.monotonic()<end:
        while GLib.MainContext.default().pending():
            GLib.MainContext.default().iteration(False)
        time.sleep(.01)
x=C.CDLL('libX11.so.6'); xt=C.CDLL('libXtst.so.6')
x.XOpenDisplay.restype=C.c_void_p; d=x.XOpenDisplay(None)
x.XDefaultRootWindow.argtypes=[C.c_void_p]; x.XDefaultRootWindow.restype=C.c_ulong
x.XQueryTree.argtypes=[C.c_void_p,C.c_ulong,C.POINTER(C.c_ulong),C.POINTER(C.c_ulong),C.POINTER(C.POINTER(C.c_ulong)),C.POINTER(C.c_uint)]
x.XFetchName.argtypes=[C.c_void_p,C.c_ulong,C.POINTER(C.c_char_p)]
x.XFree.argtypes=[C.c_void_p]
x.XInternAtom.argtypes=[C.c_void_p,C.c_char_p,C.c_int]; x.XInternAtom.restype=C.c_ulong
x.XGetWindowProperty.argtypes=[C.c_void_p,C.c_ulong,C.c_ulong,C.c_long,C.c_long,C.c_int,C.c_ulong,C.POINTER(C.c_ulong),C.POINTER(C.c_int),C.POINTER(C.c_ulong),C.POINTER(C.c_ulong),C.POINTER(C.POINTER(C.c_ubyte))]
pid_atom=x.XInternAtom(d,b'_NET_WM_PID',0)
def belongs_to_process(window):
    actual=C.c_ulong(); fmt=C.c_int(); count=C.c_ulong(); remaining=C.c_ulong(); data=C.POINTER(C.c_ubyte)()
    x.XGetWindowProperty(d,window,pid_atom,0,1,0,0,C.byref(actual),C.byref(fmt),C.byref(count),C.byref(remaining),C.byref(data))
    try:
        return bool(data) and fmt.value==32 and count.value==1 and C.cast(data,C.POINTER(C.c_ulong))[0]==proc.pid
    finally:
        if data: x.XFree(data)
x.XSetInputFocus.argtypes=[C.c_void_p,C.c_ulong,C.c_int,C.c_ulong]
x.XFlush.argtypes=[C.c_void_p]
x.XKeysymToKeycode.argtypes=[C.c_void_p,C.c_ulong]; x.XKeysymToKeycode.restype=C.c_uint
xt.XTestFakeKeyEvent.argtypes=[C.c_void_p,C.c_uint,C.c_int,C.c_ulong]
def find_window(parent,depth=0):
    rt=C.c_ulong(); par=C.c_ulong(); children=C.POINTER(C.c_ulong)(); count=C.c_uint()
    x.XQueryTree(d,parent,C.byref(rt),C.byref(par),C.byref(children),C.byref(count))
    found=None
    for i in range(count.value):
        name=C.c_char_p(); child=children[i]
        if x.XFetchName(d,child,C.byref(name)) and name.value:
            title=name.value.decode(errors='replace'); x.XFree(name)
            if title=='Bunyi' and belongs_to_process(child): found=child; break
    if found is None and depth < 5:
        for i in range(count.value):
            found=find_window(children[i],depth+1)
            if found: break
    x.XFree(children)
    return found
def tabs():
    code=x.XKeysymToKeycode(d,0xff09)
    for keypress in range(20):
        events_before=len(events)
        print('TAB',keypress+1,phase,flush=True)
        xt.XTestFakeKeyEvent(d,code,1,0); xt.XTestFakeKeyEvent(d,code,0,0); x.XFlush(d); pump(.6)
        assert len(events)-events_before <= 1, 'Duplicate focus events for one Tab'
def walk(node,depth=0):
    if depth>25: return
    name=node.get_name(); role=node.get_role_name()
    if role in ('push button','text','combo box','radio button'):
        print('CONTROL',repr(name),role,flush=True)
    for i in range(node.get_child_count()):
        walk(node.get_child_at_index(i),depth+1)
with tempfile.TemporaryDirectory(prefix='bunyi-atspi-') as state, args.log.open('w') as log:
    env=dict(os.environ, XDG_CONFIG_HOME=state+'/config', XDG_DATA_HOME=state+'/data')
    proc=subprocess.Popen([str(app)],cwd=app.parent,stdout=log,stderr=log,env=env)
    try:
        pump(4)
        window=find_window(x.XDefaultRootWindow(d)); print('Window',window,flush=True)
        if not window: raise RuntimeError('App window missing')
        x.XSetInputFocus(d,window,1,0); x.XFlush(d); pump(.5)
        phase='before-tree-walk'; tabs()
        assert any('Preset voice' == e[3] for e in events), 'No mode focus event'
        print('Fresh-tree test finished',flush=True)
        assert any('SCRIPT' == e[3] for e in events), 'No script focus event'
        assert any('Language' == e[3] for e in events), 'No language focus event'
        assert any('Speaker' == e[3] for e in events), 'No speaker focus event'
        desktop=Atspi.get_desktop(0)
        for i in range(desktop.get_child_count()):
            candidate=desktop.get_child_at_index(i)
            if 'Avalonia' in candidate.get_name(): walk(candidate)
        phase='after-tree-walk'; tabs()
        print('PASS: fresh-tree names and no duplicate focus events; Orca speech not tested.',flush=True)
    finally:
        proc.terminate()
        try: proc.wait(timeout=5)
        except subprocess.TimeoutExpired: proc.kill()
