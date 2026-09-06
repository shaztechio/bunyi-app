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

import argparse, ctypes as C, json, os, pathlib, subprocess, tempfile, time
import gi
gi.require_version('Atspi','2.0')
from gi.repository import Atspi, GLib
parser=argparse.ArgumentParser(description='Check real Bunyi AT-SPI focus events without pre-reading controls.')
parser.add_argument('app', type=pathlib.Path)
parser.add_argument('--log', type=pathlib.Path, default=pathlib.Path('atspi-app.log'))
parser.add_argument('--expect-announcements', action='store_true')
parser.add_argument('--context', action='store_true', help='Check Settings and toolbar context with temporary model/configuration fixtures')
parser.add_argument('--orca-speech', action='store_true', help='Also require one model subtitle in Orca 49+ generated speech (implies --context)')
parser.add_argument('--settings', action='store_true', help='Check Settings navigation and Appearance instead of the main window')
args=parser.parse_args()
if args.orca_speech: args.context=True
app=args.app.resolve(strict=True)
phase='startup'
events=[]
selections=[]
focused=None
checked_pickers=set()
checked_placeholders=set()
announcements=[]
def on_event(e, *args):
    global focused
    if 'focused' in e.type:
        focused=e.source
        events.append((phase,e.type,e.detail1,e.source.get_name()))
        print('EVENT',events[-1],flush=True)
        if e.source.get_name() in ('SCRIPT','Style'):
            attrs=e.source.get_attributes(); placeholder=attrs.get('placeholder-text')
            assert placeholder, 'Missing placeholder attribute'
            assert e.source.get_description() != placeholder, 'Placeholder duplicated as description'
            assert e.source.get_child_count()==0, 'Text-box template is exposed as duplicate content'
            checked_placeholders.add(e.source.get_name())
    elif e.type == 'object:selection-changed' and e.source.get_role_name() == 'combo box':
        child=e.source.get_selection_iface().get_selected_child(0)
        value=child.get_name() if child else None
        selections.append((e.source.get_name(),value))
        print('SELECTION',selections[-1],flush=True)
    elif e.type == 'object:announcement':
        assert e.detail1==1, 'Announcement must be polite'
        assert not e.source.get_state_set().contains(Atspi.StateType.FOCUSED), 'Announcement moved focus'
        announcements.append(e.any_data)
        print('ANNOUNCEMENT',repr(e.any_data),flush=True)
listener=Atspi.EventListener.new(on_event)
listener.register('object:state-changed:focused')
listener.register('object:selection-changed')
listener.register('object:announcement')
def pump(seconds):
    end=time.monotonic()+seconds
    while time.monotonic()<end:
        while GLib.MainContext.default().pending():
            GLib.MainContext.default().iteration(False)
        time.sleep(.01)
x=C.CDLL('libX11.so.6')
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
def find_window(parent,depth=0,title_prefix='Bunyi'):
    rt=C.c_ulong(); par=C.c_ulong(); children=C.POINTER(C.c_ulong)(); count=C.c_uint()
    x.XQueryTree(d,parent,C.byref(rt),C.byref(par),C.byref(children),C.byref(count))
    found=None
    for i in range(count.value):
        name=C.c_char_p(); child=children[i]
        if x.XFetchName(d,child,C.byref(name)) and name.value:
            title=name.value.decode(errors='replace'); x.XFree(name)
            if title.startswith(title_prefix) and belongs_to_process(child): found=child; break
    if found is None and depth < 5:
        for i in range(count.value):
            found=find_window(children[i],depth+1,title_prefix)
            if found: break
    x.XFree(children)
    return found
class XKeyEvent(C.Structure):
    _fields_=[('type',C.c_int),('serial',C.c_ulong),('send_event',C.c_int),
              ('display',C.c_void_p),('window',C.c_ulong),('root',C.c_ulong),
              ('subwindow',C.c_ulong),('time',C.c_ulong),('x',C.c_int),('y',C.c_int),
              ('x_root',C.c_int),('y_root',C.c_int),('state',C.c_uint),
              ('keycode',C.c_uint),('same_screen',C.c_int)]
class XEvent(C.Union):
    _fields_=[('key',XKeyEvent),('pad',C.c_long*24)]
x.XSendEvent.argtypes=[C.c_void_p,C.c_ulong,C.c_int,C.c_long,C.POINTER(XEvent)]

def key(symbol, modifiers=()):
    # Direct X11 events keep WSLg's compositor from dropping synthetic keys.
    # They still enter Avalonia's native input path, only on our own PID.
    target=find_window(x.XDefaultRootWindow(d),title_prefix='Settings') or find_window(x.XDefaultRootWindow(d))
    assert target, 'Test window disappeared'
    x.XSetInputFocus(d,target,1,0); x.XFlush(d); pump(.1)
    event=XEvent()
    event.key.display=d; event.key.window=target
    event.key.root=x.XDefaultRootWindow(d); event.key.same_screen=1
    event.key.keycode=x.XKeysymToKeycode(d,symbol)
    event.key.state=sum({0xffe1:1,0xffe3:4,0xffe9:8}[modifier] for modifier in modifiers)
    for event_type, mask in ((2,1),(3,2)):
        event.key.type=event_type
        assert x.XSendEvent(d,target,0,mask,C.byref(event)), 'X11 rejected test key'
    x.XFlush(d); pump(.5)

def speech_strings(value):
    if isinstance(value,str):
        yield value
    elif isinstance(value,(list,tuple)):
        for child in value: yield from speech_strings(child)

def check_orca_speech(control):
    # Only change settings in this probe process, never the user's Orca profile.
    from orca import script_manager, settings, orca_platform
    assert hasattr(script_manager,'get_manager'), 'This check requires Orca 49 or later'
    script=script_manager.get_manager().get_script(control.get_application(),control)
    settings.speakDescription=True
    for tutorials in (False,True):
        settings.enableTutorialMessages=tutorials
        words=' '.join(speech_strings(script.speech_generator.generate_speech(control)))
        print('ORCA SPEECH',orca_platform.version,'tutorials='+str(tutorials),repr(words),flush=True)
        assert words.count('21 bytes')==1 and words.count('Hugging Face')==1, 'Orca generated missing or duplicate subtitle speech'

def settings_check():
    if args.context:
        for expected in ('Settings','Doctor','Logs','Help'):
            key(0xff09)
            assert focused and focused.get_name()==expected, 'Toolbar name is not concise'
            assert not focused.get_description(), 'Toolbar tooltip leaked into its description'
        print('TOOLBAR CONTEXT PASS',flush=True)
    else:
        key(0xff09); key(0xff09)
    key(0x2c,(0xffe3,)); pump(1)
    window=find_window(x.XDefaultRootWindow(d),title_prefix='Settings')
    assert window, 'Settings window missing'
    x.XSetInputFocus(d,window,1,0); x.XFlush(d); pump(.5)
    for _ in range(10):
        if focused and focused.get_name()=='APPEARANCE': break
        key(0xff09)
    assert focused and focused.get_name()=='APPEARANCE', 'Appearance unreachable'
    for symbol, expected in ((0xff54,'Light'),(0xff54,'Dark'),(0xff52,'Light'),(0xff52,'System')):
        before=len(selections)
        key(symbol)
        assert selections[before:]==[('APPEARANCE',expected)], 'Missing or duplicate Appearance selection: '+repr(selections[before:])
        assert focused.get_name()=='APPEARANCE', 'Theme change lost keyboard focus'
    key(0xff09,(0xffe1,))
    assert focused.get_name()=='General', 'Shift+Tab did not return to General'
    contexts={}
    headers=('General','Models','Storage','Backup','About')
    for index, header in enumerate(headers):
        assert focused.get_name()==header and focused.get_role_name()=='page tab', 'Wrong tab header focus'
        before=len(events)
        key(0xff09)
        assert len(events)>before and focused.get_role_name()!='page tab', 'Tab failed to enter '+header
        key(0xff09,(0xffe1,))
        assert focused.get_name()==header, 'Shift+Tab failed to return to '+header
        key(0xff09)
        for _ in range(60):
            if focused.get_role_name()=='page tab': break
            if args.context:
                name=focused.get_name(); description=focused.get_description()
                contexts[name]=description
                print('CONTEXT',repr(name),repr(description),flush=True)
                assert not description or name != description, 'Action repeated as its description'
                if name.startswith('Move probe/') and ' to the Trash.' in name:
                    assert not description and not focused.get_help_text(), 'Subtitle duplicated in AT-SPI Description/HelpText'
                    if args.orca_speech: check_orca_speech(focused)
                    row=focused.get_parent()
                    assert not row.get_name(), 'Model row acquired a spoken layout name'
                    assert row.get_child_count()==1 and row.get_child_at_index(0)==focused, 'Model metadata exposed beside its action as duplicate context'

                parent=focused.get_parent()
                while parent and parent.get_role_name() not in ('frame','application'):
                    assert parent.get_name() not in ('ItemsPresenter','ItemsControl','ScrollViewer','Panel','StackPanel','ContentPresenter'), 'Layout name leaked into context'
                    parent=parent.get_parent()
                if name=='Configuration name':
                    for char in 'probe': key(ord(char))
            before=len(events)
            key(0xff09)
            assert len(events)>before, 'Tab trapped in '+header
        assert focused.get_name()==header, 'Tab failed to wrap to '+header
        print('SETTINGS TAB PASS',header,flush=True)
        if index < len(headers)-1: key(0xff53)
    if args.context:
        required={
            'APPEARANCE':'System follows your computer.',
            'Free memory when switching modes':'Each mode uses its own model',
            'Preset voice':'Hugging Face repository',
            'Voice design':'Hugging Face repository',
            'Voice clone':'Hugging Face repository',
            'Configuration name':'The three go together',
            'Save configuration probe':'three model sources',
            'Restore configuration Probe server':'example.com',
            'Delete configuration Probe server':'example.com',
            'Models folder':'Models are large',
            'Choose models folder':'/Bunyi/Models',
            'Show models folder in the file manager':'/Bunyi/Models',
            'Use default models folder':'/Bunyi/Models',
            'Back up models':'Collects your models folder',
            'Restore models from backup':'Restoring never replaces',
            'Copy Bunyi version and platform':'Version ',
        }
        for name, fragment in required.items():
            assert any((key==name or key.startswith(name+': ')) and fragment in value for key,value in contexts.items()), 'Missing context for '+name
        for model in ('voice-one','voice-two','voice-three'):
            matches=[name for name in contexts if name.startswith('Move probe/'+model+' to the Trash. ')]
            assert len(matches)==1, 'Model action missing'
            name=matches[0]
            assert name.count('Hugging Face')==1 and name.count('21 bytes')==1 and not contexts[name], 'Missing or duplicate model context'
        links={name:description for name,description in contexts.items() if name.startswith('Open ') and name!='Open Bunyi website'}
        assert len(links)>5 and all('License:' in description for description in links.values()), 'Missing credit context'
        downloads=[name for name in contexts if name.startswith('Download ') and ' in advance: ' in name]
        assert len(downloads)==3, 'Download commands lack mode context'
        print('PASS: Settings row, field, action and credit context; short toolbar names; no duplicate descriptions or layout names.',flush=True)
    key(0xff51)
    assert focused.get_name()=='Backup' , 'Left did not select the previous tab'
    speech_result='Orca generated subtitle speech verified.' if args.orca_speech else 'Orca speech not tested.'
    print('PASS: Settings tab return in both directions and single named Appearance events; '+speech_result,flush=True)

def tabs():
    for keypress in range(20):
        events_before=len(events)
        print('TAB',keypress+1,phase,flush=True)
        key(0xff09)
        assert len(events)-events_before <= 1, 'Duplicate focus events for one Tab'
        if focused and focused.get_name() in ('Language','Speaker') and focused.get_name() not in checked_pickers:
            picker=focused; picker_name=picker.get_name(); checked_pickers.add(picker_name)
            child=picker.get_selection_iface().get_selected_child(0)
            assert child and child.get_name(), 'Collapsed selection is missing'
            initial=child.get_name(); selection_count=len(selections)
            for symbol in (0xff54,0xff52):
                key(symbol)
            changes=selections[selection_count:]
            assert changes and any(value != initial for _,value in changes), 'Arrow keys did not announce a new selection'
            assert len(changes)<=2 and all(value for _,value in changes), 'Invalid or duplicate selection events'
            before_open=len(events)
            key(0xff54,(0xffe9,))
            key(0xff54)
            assert any(e[3] and e[3] != picker_name for e in events[before_open:]), 'Opened picker lacks focused item event'
            key(0xff1b)
            print('OPEN PICKER PASS',picker_name,flush=True)
def walk(node,depth=0):
    if depth>25: return
    name=node.get_name(); role=node.get_role_name()
    assert name not in ('Panel','StackPanel','Grid','Border','ContentPresenter','ScrollContentPresenter','VisualLayerManager','DockPanel','WrapPanel'), 'Decorative class name leaked: '+name
    if role in ('push button','text','combo box','radio button'):
        print('CONTROL',repr(name),role,flush=True)
    for i in range(node.get_child_count()):
        walk(node.get_child_at_index(i),depth+1)
if args.orca_speech:
    os.environ.setdefault('GDK_BACKEND','x11')
    gi.require_version('Gtk','3.0')
    from gi.repository import Gtk
    Gtk.init([])
    # Match Orca's entry-point import order; importing script_manager first
    # can trigger circular imports in Orca 49.
    from orca import debug, debugging_tools_manager, messages

with tempfile.TemporaryDirectory(prefix='bunyi-atspi-') as state, args.log.open('w') as log:
    env=dict(os.environ, XDG_CONFIG_HOME=state+'/config', XDG_DATA_HOME=state+'/data')
    if args.context:
        models=pathlib.Path(state)/'data/Bunyi/Models'
        for name in ('voice-one','voice-two','voice-three'):
            folder=models/'models/probe'/name
            folder.mkdir(parents=True)
            (folder/'fixture.txt').write_text('model listing fixture')
        configs=pathlib.Path(state)/'data/Bunyi/ModelConfigs'
        configs.mkdir(parents=True)
        (configs/'configs.json').write_text(json.dumps([{'id':'01234567-89ab-cdef-0123-456789abcdef','name':'Probe server','presetVoice':'https://example.com/preset'}]))
        settings=pathlib.Path(state)/'config/Bunyi'
        settings.mkdir(parents=True)
        (settings/'settings.json').write_text(json.dumps({'modelsFolder':str(models)}))
    proc=subprocess.Popen([str(app)],cwd=app.parent,stdout=log,stderr=log,env=env)
    try:
        pump(4)
        window=find_window(x.XDefaultRootWindow(d)); print('Window',window,flush=True)
        if not window: raise RuntimeError('App window missing')
        x.XSetInputFocus(d,window,1,0); x.XFlush(d); pump(.5)
        if args.settings or args.context:
            settings_check()
        else:
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
            assert checked_pickers == {'Language','Speaker'}, 'Picker checks did not run'
            assert checked_placeholders == {'SCRIPT','Style'}, 'Placeholder checks did not run'
            if args.expect_announcements:
                expected=['Generating','Generating… 24 frames · 2.0s of speech so far','Ready']
                assert announcements==expected, 'Announcements missing, duplicated or out of order: '+repr(announcements)
            print('PASS: focus, layout, pickers, placeholders and requested announcements; Orca speech not tested.',flush=True)
    finally:
        proc.terminate()
        try: proc.wait(timeout=5)
        except subprocess.TimeoutExpired: proc.kill()
